using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Path = System.IO.Path;

namespace GUtv_backend_dotnet.Tests;

public class UserAvatarTests
{
    [Fact]
    public async Task UploadReencodesAndPersistsPhotoAndRemovesMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var photo = await PhotoAsync(700, 400, metadata: true);
        var user = await fixture.Service.UploadAsync(1, photo);
        var stored = await fixture.Db.Users.AsNoTracking().SingleAsync(u => u.Id == 1);
        Assert.Equal(user.AvatarUrl, stored.AvatarUrl);
        Assert.Equal("existing-robot", stored.AvatarSeed);
        var path = fixture.Store.GetPath(user.AvatarUrl!["/avatars/".Length..])!;
        using var image = await Image.LoadAsync(path);
        Assert.Equal(512, image.Width);
        Assert.Equal(512, image.Height);
        Assert.Equal("WEBP", image.Metadata.DecodedImageFormat!.Name.ToUpperInvariant());
        Assert.Null(image.Metadata.ExifProfile);
        Assert.Null(image.Metadata.XmpProfile);
        Assert.Null((await fixture.Db.Users.AsNoTracking().SingleAsync(u => u.Id == 2)).AvatarUrl);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task AllSupportedFormatsAreAccepted(string format)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var image = new Image<Rgba32>(20, 30);
        using var stream = new MemoryStream();
        if (format == "jpeg") await image.SaveAsJpegAsync(stream);
        else if (format == "png") await image.SaveAsPngAsync(stream);
        else await image.SaveAsWebpAsync(stream);
        var url = await fixture.Store.SaveAsync(Convert.ToBase64String(stream.ToArray()));
        Assert.True(File.Exists(fixture.Store.GetPath(url["/avatars/".Length..])));
    }

    [Fact]
    public async Task ReplacementDeletesPreviousFileAndRemovalRestoresExistingRobot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var photo = await PhotoAsync(30, 30);
        var first = (await fixture.Service.UploadAsync(1, photo)).AvatarUrl!;
        var second = (await fixture.Service.UploadAsync(1, photo)).AvatarUrl!;
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(fixture.Store.GetPath(first["/avatars/".Length..])));
        Assert.True(File.Exists(fixture.Store.GetPath(second["/avatars/".Length..])));
        var removed = await fixture.Service.RemoveAsync(1);
        Assert.Null(removed.AvatarUrl);
        Assert.Equal("existing-robot", removed.AvatarSeed);
        Assert.Empty(Directory.GetFiles(fixture.Directory));
        Assert.Null((await fixture.Service.RemoveAsync(1)).AvatarUrl);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("PHN2Zz48c2NyaXB0PmFsZXJ0KDEpPC9zY3JpcHQ+PC9zdmc+")]
    [InlineData("aW52YWxpZC1pbWFnZQ==")]
    public async Task BadReplacementPreservesPreviousPhoto(string input)
    {
        await using var fixture = await Fixture.CreateAsync();
        var previous = (await fixture.Service.UploadAsync(1, await PhotoAsync(20, 20))).AvatarUrl;
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.UploadAsync(1, input));
        Assert.Equal(previous, (await fixture.Db.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).AvatarUrl);
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task OversizedInputAndImageDimensionsAreRejectedWithoutWritingFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Store.SaveAsync(new string('A', 7_000_000)));
        await Assert.ThrowsAsync<GraphQLException>(async () => await fixture.Store.SaveAsync(await PhotoAsync(8001, 1)));
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public async Task UnsupportedFormatAndMissingUserDoNotLeaveFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var image = new Image<Rgba32>(20, 20);
        using var output = new MemoryStream();
        await image.SaveAsGifAsync(output);
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Store.SaveAsync(Convert.ToBase64String(output.ToArray())));
        await Assert.ThrowsAsync<GraphQLException>(async () => await fixture.Service.UploadAsync(999, await PhotoAsync(20, 20)));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    [Theory]
    [InlineData("../../secret.webp")]
    [InlineData("image.svg")]
    [InlineData("00000000000000000000000000000000.webp/../../secret")]
    public async Task PublicPathCannotEscapeStorageDirectory(string filename)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Null(fixture.Store.GetPath(filename));
    }

    private static async Task<string> PhotoAsync(int width, int height, bool metadata = false)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(110, 160, 220));
        if (metadata)
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Software, "private metadata");
        }
        using var output = new MemoryStream();
        await image.SaveAsPngAsync(output);
        return Convert.ToBase64String(output.ToArray());
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db, string directory, AvatarImageStore store) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public string Directory { get; } = directory;
        public AvatarImageStore Store { get; } = store;
        public UserAvatarService Service => new(Db, Store);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            // Only the avatar's user record is needed; SQLite cannot create PostgreSQL computed columns.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Users" (
                    "Id" INTEGER PRIMARY KEY, "Login" TEXT NOT NULL, "NormalizedLogin" TEXT NOT NULL DEFAULT '',
                    "PasswordHash" TEXT NOT NULL DEFAULT '', "Name" TEXT NOT NULL, "AvatarSeed" TEXT,
                    "AvatarUrl" TEXT, "TelegramChatId" INTEGER, "TelegramUsername" TEXT,
                    "TelegramLinkCode" TEXT, "TelegramLinkCodeExpiry" TEXT,
                    "Role" INTEGER NOT NULL, "Banned" INTEGER NOT NULL, "JoinYear" INTEGER NOT NULL
                );
                INSERT INTO "Users" ("Id", "Login", "Name", "AvatarSeed", "Role", "Banned", "JoinYear")
                VALUES (1, 'one', 'First user', 'existing-robot', 0, 0, 2026), (2, 'two', 'Second user', NULL, 0, 0, 2026);
                """);
            var directory = Path.Combine(Path.GetTempPath(), "gutv-avatar-tests-" + Guid.NewGuid().ToString("N"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AvatarStorage:Path"] = directory
            }).Build();
            return new Fixture(connection, db, directory, new AvatarImageStore(config, NullLogger<AvatarImageStore>.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
