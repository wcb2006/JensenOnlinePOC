namespace JensenOnline.Api.Models;

public static class Roles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
}

public static class OrderStatus
{
    public const string Created = "Created";
    public const string Paid = "Paid";
    public const string Shipped = "Shipped";
    public const string Cancelled = "Cancelled";
}

public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = "";

    // Endast en saltad PBKDF2-hash lagras – aldrig lösenordet i klartext.
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Customer;

    // Byts ut när roll ändras, kontot spärras eller stöld av token upptäcks.
    // Alla access tokens som bär den gamla stämpeln blir då direkt ogiltiga.
    public string SecurityStamp { get; set; } = NewStamp();

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Order> Orders { get; set; } = [];
    public List<RefreshToken> RefreshTokens { get; set; } = [];

    public void RotateSecurityStamp() => SecurityStamp = NewStamp();
    private static string NewStamp() => Guid.NewGuid().ToString("N");
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }

    // Mjuk borttagning: orderhistoriken behåller sin integritet.
    public bool IsDeleted { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = OrderStatus.Created;
    public decimal Total { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    // Namn och pris kopieras vid köptillfället – priset kommer alltid från databasen, aldrig från klienten.
    public string ProductName { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
}

public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }

    // Endast SHA-256-hash lagras. Läcker databasen kan tokens inte användas.
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? CreatedByIp { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    public string? Email { get; set; }
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
