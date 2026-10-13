using System.ComponentModel.DataAnnotations;
using JensenOnline.Api.Data;
using JensenOnline.Api.Dtos;
using JensenOnline.Api.Models;
using JensenOnline.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JensenOnline.Api.Controllers;

// Hela controllern kräver Admin-rollen. Kontrollen görs på servern för varje anrop –
// att admin-menyn döljs i frontend är bara användarvänlighet, inte säkerhet.
[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin")]
public sealed class AdminController(AppDbContext db, AuditService audit) : ControllerBase
{
    [HttpGet("orders")]
    public async Task<ActionResult<IEnumerable<OrderDto>>> GetAllOrders()
    {
        var orders = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.User)
            .OrderByDescending(o => o.Id)
            .ToListAsync();
        return orders.Select(o => OrderDto.From(o, includeCustomer: true)).ToList();
    }

    [HttpPut("orders/{id:int}/status")]
    public async Task<ActionResult<OrderDto>> UpdateOrderStatus(int id, UpdateOrderStatusRequest request)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.User).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();

        var oldStatus = order.Status;
        order.Status = request.Status;
        await db.SaveChangesAsync();

        await audit.LogAsync("OrderStatusChanged", true, User.GetUserId(), User.Identity?.Name, $"Order {id}: {oldStatus} -> {request.Status}");
        return OrderDto.From(order, includeCustomer: true);
    }

    [HttpGet("users")]
    public async Task<ActionResult<IEnumerable<AdminUserDto>>> GetUsers()
    {
        var now = DateTime.UtcNow;
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
        return users.Select(u => new AdminUserDto(u.Id, u.Email, u.Role, u.IsDisabled, u.LockoutEndUtc > now, u.FailedLoginCount, u.CreatedAtUtc)).ToList();
    }

    [HttpPut("users/{id:int}/role")]
    public async Task<IActionResult> UpdateRole(int id, UpdateRoleRequest request)
    {
        var adminId = User.GetUserId();
        if (id == adminId)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Otillåten åtgärd.", detail: "Du kan inte ändra din egen roll.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        var oldRole = user.Role;
        user.Role = request.Role;
        // Ny security stamp => användarens befintliga tokens (med gammal roll) slutar gälla direkt.
        user.RotateSecurityStamp();
        await RevokeRefreshTokensAsync(id);
        await db.SaveChangesAsync();

        await audit.LogAsync("UserRoleChanged", true, adminId, User.Identity?.Name, $"Användare {id} ({user.Email}): {oldRole} -> {request.Role}");
        return NoContent();
    }

    [HttpPut("users/{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, UpdateUserStatusRequest request)
    {
        var adminId = User.GetUserId();
        if (id == adminId)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Otillåten åtgärd.", detail: "Du kan inte spärra ditt eget konto.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        user.IsDisabled = request.IsDisabled!.Value;
        if (!user.IsDisabled)
        {
            user.LockoutEndUtc = null;
            user.FailedLoginCount = 0;
        }
        user.RotateSecurityStamp();
        await RevokeRefreshTokensAsync(id);
        await db.SaveChangesAsync();

        await audit.LogAsync(user.IsDisabled ? "UserDisabled" : "UserEnabled", true, adminId, User.Identity?.Name, $"Användare {id} ({user.Email})");
        return NoContent();
    }

    [HttpGet("audit-logs")]
    public async Task<ActionResult<IEnumerable<AuditLogDto>>> GetAuditLogs([FromQuery, Range(1, 500)] int take = 100)
    {
        var logs = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync();
        return logs.Select(a => new AuditLogDto(a.Id, a.TimestampUtc, a.UserId, a.Email, a.Action, a.Success, a.Details, a.IpAddress)).ToList();
    }

    private async Task RevokeRefreshTokensAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAtUtc == null).ToListAsync();
        foreach (var token in active) token.RevokedAtUtc = now;
    }
}
