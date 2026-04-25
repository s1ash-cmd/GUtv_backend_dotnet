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
    public DbSet<EqPhoto> EqPhotos => Set<EqPhoto>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<EqItem>()
            .HasIndex(e => e.InventoryNumber)
            .IsUnique();

        modelBuilder.Entity<Cart>()
            .HasIndex(c => c.UserId)
            .IsUnique();

        modelBuilder.Entity<CartItem>()
            .HasIndex(ci => new { ci.CartId, ci.EqModelId })
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

        modelBuilder.Entity<Cart>()
            .HasOne(c => c.User)
            .WithOne(u => u.Cart)
            .HasForeignKey<Cart>(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CartItem>()
            .HasOne(ci => ci.Cart)
            .WithMany(c => c.Items)
            .HasForeignKey(ci => ci.CartId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CartItem>()
            .HasOne(ci => ci.EqModel)
            .WithMany(m => m.CartItems)
            .HasForeignKey(ci => ci.EqModelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
