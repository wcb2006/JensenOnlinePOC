using JensenOnline.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace JensenOnline.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(254).IsRequired();
            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.Role).HasMaxLength(20).IsRequired();
            e.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();
        });

        b.Entity<Product>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(100).IsRequired();
            e.Property(p => p.Description).HasMaxLength(1000);
            e.Property(p => p.Category).HasMaxLength(50).IsRequired();
            e.Property(p => p.Price).HasPrecision(18, 2);
        });

        b.Entity<Order>(e =>
        {
            e.HasOne(o => o.User).WithMany(u => u.Orders).HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(o => o.Status).HasMaxLength(20).IsRequired();
            e.Property(o => o.Total).HasPrecision(18, 2);
            e.HasIndex(o => o.UserId);
        });

        b.Entity<OrderItem>(e =>
        {
            e.HasOne(i => i.Order).WithMany(o => o.Items).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(i => i.ProductName).HasMaxLength(100).IsRequired();
            e.Property(i => i.UnitPrice).HasPrecision(18, 2);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(t => t.ReplacedByTokenHash).HasMaxLength(64);
            e.Property(t => t.CreatedByIp).HasMaxLength(45);
            e.HasOne(t => t.User).WithMany(u => u.RefreshTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>(e =>
        {
            e.Property(a => a.Action).HasMaxLength(50).IsRequired();
            e.Property(a => a.Email).HasMaxLength(254);
            e.Property(a => a.Details).HasMaxLength(500);
            e.Property(a => a.IpAddress).HasMaxLength(45);
            e.HasIndex(a => a.TimestampUtc);
        });
    }
}
