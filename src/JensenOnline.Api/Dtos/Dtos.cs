using System.ComponentModel.DataAnnotations;
using JensenOnline.Api.Models;

namespace JensenOnline.Api.Dtos;

// Inkommande DTO:er är separata från entiteterna. Klienten kan därför aldrig sätta fält som
// Role, Price på orderrader eller UserId (skydd mot mass assignment / over-posting).
// Okända JSON-fält avvisas dessutom globalt (se Program.cs).

// ---------- Auth ----------

public sealed class RegisterRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = "";

    [Required, StringLength(128)]
    public string Password { get; init; } = "";
}

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = "";

    [Required, StringLength(128)]
    public string Password { get; init; } = "";
}

public sealed record UserDto(int Id, string Email, string Role)
{
    public static UserDto From(AppUser u) => new(u.Id, u.Email, u.Role);
}

public sealed record AuthResponse(string AccessToken, DateTime AccessTokenExpiresUtc, UserDto User);

// ---------- Produkter ----------

public sealed class ProductRequest
{
    // Vitlista: bokstäver (inkl. åäö), siffror och vanliga skiljetecken. Tecken som < > " ' avvisas.
    [Required, StringLength(100, MinimumLength = 2)]
    [RegularExpression(@"^[\p{L}\p{N} .,:!?&()+/\-]+$", ErrorMessage = "Namnet innehåller otillåtna tecken.")]
    public string Name { get; init; } = "";

    // Fritext tillåts, men all output kodas i frontend (textContent) och API:t svarar bara med JSON.
    [StringLength(1000)]
    public string Description { get; init; } = "";

    [Required, StringLength(50, MinimumLength = 2)]
    [RegularExpression(@"^[\p{L}\p{N} &\-]+$", ErrorMessage = "Kategorin innehåller otillåtna tecken.")]
    public string Category { get; init; } = "";

    [Range(typeof(decimal), "0.01", "1000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Priset måste vara mellan 0,01 och 1 000 000.")]
    public decimal Price { get; init; }

    [Range(0, 100_000, ErrorMessage = "Lagersaldot måste vara mellan 0 och 100 000.")]
    public int Stock { get; init; }
}

public sealed record ProductDto(int Id, string Name, string Description, string Category, decimal Price, int Stock)
{
    public static ProductDto From(Product p) => new(p.Id, p.Name, p.Description, p.Category, p.Price, p.Stock);
}

// ---------- Order ----------

public sealed class CreateOrderRequest
{
    [Required, MinLength(1, ErrorMessage = "Ordern måste innehålla minst en produkt."), MaxLength(20)]
    public List<OrderItemRequest> Items { get; init; } = [];
}

public sealed class OrderItemRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; init; }

    [Range(1, 10, ErrorMessage = "Antal måste vara mellan 1 och 10.")]
    public int Quantity { get; init; }
}

public sealed record OrderItemDto(int ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record OrderDto(int Id, DateTime CreatedAtUtc, string Status, decimal Total, IReadOnlyList<OrderItemDto> Items, string? CustomerEmail)
{
    public static OrderDto From(Order o, bool includeCustomer) => new(
        o.Id, o.CreatedAtUtc, o.Status, o.Total,
        o.Items.Select(i => new OrderItemDto(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, i.UnitPrice * i.Quantity)).ToList(),
        includeCustomer ? o.User?.Email : null);
}

// ---------- Admin ----------

public sealed class UpdateRoleRequest
{
    [Required, RegularExpression("^(Customer|Admin)$", ErrorMessage = "Ogiltig roll.")]
    public string Role { get; init; } = "";
}

public sealed class UpdateUserStatusRequest
{
    [Required]
    public bool? IsDisabled { get; init; }
}

public sealed class UpdateOrderStatusRequest
{
    [Required, RegularExpression("^(Created|Paid|Shipped|Cancelled)$", ErrorMessage = "Ogiltig status.")]
    public string Status { get; init; } = "";
}

public sealed record AdminUserDto(int Id, string Email, string Role, bool IsDisabled, bool IsLockedOut, int FailedLoginCount, DateTime CreatedAtUtc);

public sealed record AuditLogDto(int Id, DateTime TimestampUtc, int? UserId, string? Email, string Action, bool Success, string? Details, string? IpAddress);
