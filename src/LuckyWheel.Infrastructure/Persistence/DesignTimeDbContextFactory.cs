using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace LuckyWheel.Infrastructure.Persistence;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var basePath = Path.Combine(Directory.GetCurrentDirectory(), "../LuckyWheel.Api");
        if (!Directory.Exists(basePath))
        {
            basePath = Directory.GetCurrentDirectory();
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddUserSecrets("8960ad78-d5c4-4378-8350-7ec592132261")
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Could not find connection string 'DefaultConnection'. Ensure User Secrets or environment variable 'ConnectionStrings__DefaultConnection' is configured.");
        }

        var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
        builder.UseSqlServer(connectionString, sqlOptions =>
        {
            sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
        });

        return new ApplicationDbContext(builder.Options);
    }
}
