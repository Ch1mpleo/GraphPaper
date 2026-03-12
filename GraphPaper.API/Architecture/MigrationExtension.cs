using GraphPaper.Domain;
using GraphPaper.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GraphPaper.API.Architecture
{
    public static class MigrationExtensions
    {
        public static void ApplyMigrations(this IApplicationBuilder app, ILogger _logger)
        {
            try
            {
                _logger.LogInformation("Applying migrations...");
                using var scope = app.ApplicationServices.CreateScope();

                using var dbContext =
                    scope.ServiceProvider.GetRequiredService<GraphPaperDbContext>();

                dbContext.Database.Migrate();
                _logger.LogInformation("Migrations applied successfully!");

                SeedAdminUser(dbContext, _logger);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An problem occurred during migration!");
            }
        }

        private static void SeedAdminUser(GraphPaperDbContext dbContext, ILogger logger)
        {
            const string adminEmail = "admin@graphpaper.com";

            if (dbContext.Users.Any(u => u.Email == adminEmail))
            {
                logger.LogInformation("Admin user already exists. Skipping seed.");
                return;
            }

            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                Email = adminEmail,
                HashedPassword = new PasswordHasher().HashPassword("Admin@123")!,
                Role = "Admin",
                CreatedAt = DateTime.UtcNow
            };

            dbContext.Users.Add(admin);
            dbContext.SaveChanges();

            logger.LogInformation("Admin user seeded successfully ({Email}).", adminEmail);
        }
    }
}
