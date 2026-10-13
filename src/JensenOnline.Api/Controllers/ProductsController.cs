using System.ComponentModel.DataAnnotations;
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
[Route("api/products")]
public sealed class ProductsController(AppDbContext db, AuditService audit) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductDto>>> GetAll(
        [FromQuery, StringLength(50)] string? search,
        [FromQuery, StringLength(50)] string? category)
    {
        var query = db.Products.AsNoTracking().Where(p => !p.IsDeleted);

        // LINQ via EF Core blir alltid parametriserad SQL – söktexten behandlas som data, aldrig som SQL-kod.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(term) || p.Description.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(p => p.Category == category);

        var products = await query.OrderBy(p => p.Name).ToListAsync();
        return products.Select(ProductDto.From).ToList();
    }

    [AllowAnonymous]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductDto>> GetById(int id)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);
        return product is null ? NotFound() : ProductDto.From(product);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    public async Task<ActionResult<ProductDto>> Create(ProductRequest request)
    {
        var product = new Product();
        Apply(product, request);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        await audit.LogAsync("ProductCreated", true, User.GetUserId(), User.Identity?.Name, $"Produkt {product.Id} '{product.Name}'");
        return CreatedAtAction(nameof(GetById), new { id = product.Id }, ProductDto.From(product));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPut("{id:int}")]
    public async Task<ActionResult<ProductDto>> Update(int id, ProductRequest request)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);
        if (product is null) return NotFound();

        var oldPrice = product.Price;
        Apply(product, request);
        await db.SaveChangesAsync();

        await audit.LogAsync("ProductUpdated", true, User.GetUserId(), User.Identity?.Name,
            string.Create(CultureInfo.InvariantCulture, $"Produkt {product.Id}, pris {oldPrice} -> {product.Price}"));
        return ProductDto.From(product);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);
        if (product is null) return NotFound();

        product.IsDeleted = true;
        product.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await audit.LogAsync("ProductDeleted", true, User.GetUserId(), User.Identity?.Name, $"Produkt {product.Id} '{product.Name}'");
        return NoContent();
    }

    private static void Apply(Product product, ProductRequest request)
    {
        product.Name = request.Name.Trim();
        product.Description = request.Description.Trim();
        product.Category = request.Category.Trim();
        product.Price = request.Price;
        product.Stock = request.Stock;
        product.UpdatedAtUtc = DateTime.UtcNow;
    }
}
