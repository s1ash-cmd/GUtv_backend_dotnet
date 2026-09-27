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
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementDelivery> AnnouncementDeliveries => Set<AnnouncementDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Announcement>().HasIndex(a => a.RequestId).IsUnique();
        modelBuilder.Entity<Announcement>().Property(a => a.Title).HasMaxLength(100);
        modelBuilder.Entity<Announcement>().Property(a => a.Body).HasMaxLength(3000);
        modelBuilder.Entity<AnnouncementDelivery>().HasKey(d => new { d.AnnouncementId, d.ChatId });
        modelBuilder.Entity<AnnouncementDelivery>().HasIndex(d => new { d.Failed, d.SentAt, d.NextAttemptAt });
        modelBuilder.Entity<AnnouncementDelivery>().Property(d => d.LastError).HasMaxLength(1000);
        modelBuilder.Entity<AnnouncementDelivery>().HasOne(d => d.Announcement).WithMany()
            .HasForeignKey(d => d.AnnouncementId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<User>()
            .Property(u => u.NormalizedLogin)
            .HasComputedColumnSql("lower(btrim(\"Login\"))", stored: true);

        modelBuilder.Entity<User>()
            .HasIndex(u => u.NormalizedLogin)
            .IsUnique();

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
