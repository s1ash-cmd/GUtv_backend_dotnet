using GUtv_backend_dotnet.Models;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<EqModel> EqModels => Set<EqModel>();
    public DbSet<EqItem> EqItems => Set<EqItem>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingItem> BookingItems => Set<BookingItem>();
    public DbSet<Event> Events => Set<Event>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<EqItem>()
            .HasIndex(e => e.InventoryNumber)
            .IsUnique();

        modelBuilder.Entity<BookingItem>()
            .HasOne(bi => bi.EqItem)
            .WithMany(e => e.BookingItems)
            .HasForeignKey(bi => bi.EqItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<BookingItem>()
            .HasOne(bi => bi.Booking)
            .WithMany(b => b.BookingItems)
            .HasForeignKey(bi => bi.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EqItem>()
            .HasOne(i => i.EqModel)
            .WithMany(m => m.EqItems)
            .HasForeignKey(i => i.EqModelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}