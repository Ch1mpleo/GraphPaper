using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace GraphPaper.Domain;

public class GraphPaperDbContextFactory : IDesignTimeDbContextFactory<GraphPaperDbContext>
{
    public GraphPaperDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "GraphPaper.API"))
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        var optionsBuilder = new DbContextOptionsBuilder<GraphPaperDbContext>();
        optionsBuilder.UseNpgsql(connectionString, sql =>
        {
            sql.MigrationsAssembly(typeof(GraphPaperDbContext).Assembly.FullName);
            sql.UseVector();
        });

        return new GraphPaperDbContext(optionsBuilder.Options);
    }
}
