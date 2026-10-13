using System.Globalization;
using JensenOnline.Api.Data;
using JensenOnline.Api.Dtos;
using JensenOnline.Api.Models;
using JensenOnline.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JensenOnline.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrdersController(AppDbContext db, AuditService audit) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest request)
    {
        var userId = User.GetUserId();
        var productIds = request.Items.Select(i => i.ProductId).ToList();
        if (productIds.Distinct().Count() != productIds.Count)
        {
            ModelState.AddModelError(nameof(request.Items), "Samma produkt får bara förekomma en gång.");
            return ValidationProblem(ModelState);
        }

        var products = await db.Products
            .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id);

        foreach (var item in request.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
                ModelState.AddModelError(nameof(request.Items), $"Produkt {item.ProductId} finns inte.");
            else if (product.Stock < item.Quantity)
                ModelState.AddModelError(nameof(request.Items), $"Otillräckligt lager för {product.Name}.");
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        // Ägare = inloggad användare (från token). Pris = från databasen. Klienten styr bara produkt-id och antal.
        var order = new Order { UserId = userId, Status = OrderStatus.Created, CreatedAtUtc = DateTime.UtcNow };
        foreach (var item in request.Items)
        {
            var product = products[item.ProductId];
            product.Stock -= item.Quantity;
            order.Items.Add(new OrderItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = product.Price,
                Quantity = item.Quantity,
            });
        }
        order.Total = order.Items.Sum(i => i.UnitPrice * i.Quantity);

        // Betalning sker hos extern leverantör (Klarna) – mockas i POC:n. Ordern skapas med status Created.
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await audit.LogAsync("OrderCreated", true, userId, User.Identity?.Name,
            string.Create(CultureInfo.InvariantCulture, $"Order {order.Id}, total {order.Total}"));
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, OrderDto.From(order, includeCustomer: false));
    }

    [HttpGet("mine")]
    public async Task<ActionResult<IEnumerable<OrderDto>>> GetMine()
    {
        var userId = User.GetUserId();
        var orders = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.Id)
            .ToListAsync();
        return orders.Select(o => OrderDto.From(o, includeCustomer: false)).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderDto>> GetById(int id)
    {
        var userId = User.GetUserId();
        var isAdmin = User.IsInRole(Roles.Admin);

        // Skydd mot IDOR: ägarkontrollen ligger i själva databasfrågan.
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == id && (isAdmin || o.UserId == userId));

        if (order is null)
        {
            if (await db.Orders.AnyAsync(o => o.Id == id))
                await audit.LogAsync("OrderAccessDenied", false, userId, User.Identity?.Name, $"Försökte läsa order {id} som tillhör annan kund.");

            // 404 i stället för 403 – avslöjar inte att ordern existerar.
            return NotFound();
        }

        return OrderDto.From(order, includeCustomer: isAdmin);
    }
}
