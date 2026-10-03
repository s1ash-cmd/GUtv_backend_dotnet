using GUtv_backend_dotnet.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GUtv_backend_dotnet.Tests;

public class MigrationTests
{
    private const string CartMigration = "20260412171402_AddCartSync";
    private const string CartEditingMigration = "20260828101054_AddCartEditingBooking";

    [Fact]
    public void CartSchemaMigrationIsDiscoverableBeforeItsDependentMigration()
    {
        using var db = CreateContext();
        var migrations = db.Database.GetMigrations().ToList();

        Assert.Contains(CartMigration, migrations);
        Assert.True(migrations.IndexOf(CartMigration) < migrations.IndexOf(CartEditingMigration));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullMigrationScriptCreatesCartTablesBeforeAlteringThem(bool idempotent)
    {
        using var db = CreateContext();
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;
        var script = db.GetService<IMigrator>().GenerateScript(options: options);

        var cartsCreation = script.IndexOf("CREATE TABLE IF NOT EXISTS \"Carts\"", StringComparison.Ordinal);
        var itemsCreation = script.IndexOf("CREATE TABLE IF NOT EXISTS \"CartItems\"", StringComparison.Ordinal);
        var cartsAlteration = script.IndexOf("ALTER TABLE \"Carts\" ADD COLUMN", StringComparison.Ordinal);

        Assert.True(cartsCreation >= 0, "The complete script must create Carts on a fresh database.");
        Assert.True(itemsCreation > cartsCreation, "CartItems depends on Carts.");
        Assert.True(cartsAlteration > itemsCreation, "The editing column must be added after both cart tables exist.");
        Assert.Contains($"VALUES ('{CartMigration}'", script);
    }

    [Fact]
    public void CartMigrationCanAdoptManuallyCreatedTablesWithoutDroppingData()
    {
        using var db = CreateContext();
        var script = db.GetService<IMigrator>().GenerateScript(
            "20260316193142_eqPhoto", CartMigration);

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"Carts\"", script);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"CartItems\"", script);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Carts_UserId\"", script);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_CartItems_CartId_EqModelId\"", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("TRUNCATE", script);
        Assert.DoesNotContain("DELETE FROM", script);
    }

    [Fact]
    public void CartEditingMigrationCanAdoptAnExistingEditingColumn()
    {
        using var db = CreateContext();
        var script = db.GetService<IMigrator>().GenerateScript(
            "20260603120000_AddUserAvatarSeed", CartEditingMigration);

        Assert.Contains("ALTER TABLE \"Carts\" ADD COLUMN IF NOT EXISTS \"EditingBookingId\" integer", script);
    }

    [Fact]
    public void BookingRevisionMigrationPreservesExistingBookingsWithAnInitialRevision()
    {
        using var db = CreateContext();
        var migration = Assert.Single(db.Database.GetMigrations(), name => name.EndsWith("_AddBookingRevision"));
        var sql = db.GetService<IMigrator>().GenerateScript("20261001000000_ClearSyntheticAdminComments", migration);
        Assert.Contains("ADD \"Revision\" integer NOT NULL DEFAULT 1", sql.Replace("ADD COLUMN", "ADD"));
        Assert.DoesNotContain("DROP", sql);
        var property = db.Model.FindEntityType(typeof(GUtv_backend_dotnet.Models.Booking))!.FindProperty("Revision")!;
        Assert.True(property.IsConcurrencyToken);
        Assert.Equal(1, property.GetDefaultValue());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void SessionMigrationRemovesPlaintextTokensAndCreatesIndexedSessionStorage()
    {
        using var db = CreateContext();
        var migration = Assert.Single(db.Database.GetMigrations(), name => name.EndsWith("_AddUserSessions"));
        var sql = db.GetService<IMigrator>().GenerateScript("20261003172311_AddBookingRevision", migration);
        Assert.Contains("CREATE TABLE \"UserSessions\"", sql);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_UserSessions_RefreshTokenHash\"", sql);
        Assert.Contains("DROP COLUMN \"RefreshToken\"", sql);
        Assert.Contains("DROP COLUMN \"RefreshTokenExpiryTime\"", sql);
        Assert.DoesNotContain("DROP TABLE \"Users\"", sql);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            // Generating SQL and enumerating migration metadata never opens a connection.
            .UseNpgsql("Host=localhost;Database=migration_regression_unused;Username=unused;Password=unused")
            .Options);
}
