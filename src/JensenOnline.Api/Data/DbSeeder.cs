using JensenOnline.Api.Models;
using Microsoft.AspNetCore.Identity;

namespace JensenOnline.Api.Data;

public static class DbSeeder
{
    public static void Seed(AppDbContext db, IPasswordHasher<AppUser> hasher, IConfiguration config, ILogger logger)
    {
        if (!db.Users.Any())
        {
            // Demolösenord hämtas från konfiguration (appsettings.Development.json / miljövariabler),
            // aldrig hårdkodade i källkoden. I produktion skapas admin via en separat, loggad process.
            var adminPassword = config["Seed:AdminPassword"];
            var customerPassword = config["Seed:CustomerPassword"];

            if (string.IsNullOrWhiteSpace(adminPassword) || string.IsNullOrWhiteSpace(customerPassword))
            {
                logger.LogWarning("Seed-lösenord saknas i konfigurationen – inga demoanvändare skapas.");
            }
            else
            {
                AddUser(db, hasher, "admin@jensenonline.se", adminPassword, Roles.Admin);
                AddUser(db, hasher, "kund@jensenonline.se", customerPassword, Roles.Customer);
                AddUser(db, hasher, "kund2@jensenonline.se", customerPassword, Roles.Customer);
            }
        }

        if (!db.Products.Any())
        {
            db.Products.AddRange(
                new Product { Name = "Bärbar dator 14 tum", Category = "Datorer", Price = 8995m, Stock = 25, Description = "Lätt kontorsdator med 16 GB RAM och 512 GB SSD." },
                new Product { Name = "Skärm 27 tum", Category = "Skärmar", Price = 2495m, Stock = 40, Description = "27-tums IPS-skärm med 1440p-upplösning och USB-C." },
                new Product { Name = "Trådlöst tangentbord", Category = "Tillbehör", Price = 599m, Stock = 100, Description = "Nordisk layout, laddningsbart via USB-C." },
                new Product { Name = "Ergonomisk mus", Category = "Tillbehör", Price = 449m, Stock = 80, Description = "Vertikal mus som avlastar handleden." },
                new Product { Name = "Headset med brusreducering", Category = "Ljud", Price = 1299m, Stock = 30, Description = "Perfekt för Teams-möten i öppet landskap." },
                new Product { Name = "Kontorsstol Pro", Category = "Kontorsmöbler", Price = 3495m, Stock = 12, Description = "Justerbart ländryggsstöd och armstöd." },
                new Product { Name = "Laserskrivare", Category = "Skrivare", Price = 1895m, Stock = 15, Description = "Svartvit laserskrivare med dubbelsidig utskrift." },
                new Product { Name = "Dockningsstation USB-C", Category = "Tillbehör", Price = 1695m, Stock = 35, Description = "Två skärmutgångar, nätverk och 100 W laddning." });
        }

        db.SaveChanges();
    }

    private static void AddUser(AppDbContext db, IPasswordHasher<AppUser> hasher, string email, string password, string role)
    {
        var user = new AppUser { Email = email, Role = role };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
    }
}
