using System;
using System.Collections.Generic;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LuckyWheel.IntegrationTests.Persistence;

public class TestDatabaseFixture : IDisposable
{
    private const string DevelopmentDatabaseName = "LuckyWheelDb";
    private const string IntegrationDatabaseName = "LuckyWheelDb_IntegrationTests";
    private const string ExplicitEnvVarName = "LUCKYWHEEL_TEST_SQL_CONNECTION";

    private static readonly HashSet<string> SystemDatabaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "master",
        "model",
        "msdb",
        "tempdb"
    };

    private static readonly object _lock = new();
    private static bool _databaseInitialized;

    public string ConnectionString { get; }

    public TestDatabaseFixture()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("8960ad78-d5c4-4378-8350-7ec592132261")
            .AddEnvironmentVariables()
            .Build();

        var explicitTestConn = Environment.GetEnvironmentVariable(ExplicitEnvVarName);
        if (string.IsNullOrWhiteSpace(explicitTestConn))
        {
            explicitTestConn = config.GetConnectionString("IntegrationTestsConnection");
        }

        SqlConnectionStringBuilder connectionBuilder;
        if (!string.IsNullOrWhiteSpace(explicitTestConn))
        {
            connectionBuilder = new SqlConnectionStringBuilder(explicitTestConn);
            AssertSafeLocalSqlServer(connectionBuilder);

            var catalog = (connectionBuilder.InitialCatalog ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(catalog)
                || SystemDatabaseNames.Contains(catalog)
                || string.Equals(catalog, DevelopmentDatabaseName, StringComparison.OrdinalIgnoreCase)
                || (!catalog.EndsWith("_IntegrationTests", StringComparison.OrdinalIgnoreCase)
                    && !catalog.EndsWith("_Tests", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Explicit test SQL connection string must target a dedicated test database ending with '_IntegrationTests' or '_Tests' (never '{DevelopmentDatabaseName}' or system databases). Got '{catalog}'.");
            }

            connectionBuilder.InitialCatalog = catalog;
        }
        else
        {
            var baseConn = config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                throw new InvalidOperationException(
                    $"SQL test connection string is missing. Set '{ExplicitEnvVarName}' (or ConnectionStrings:IntegrationTestsConnection / DefaultConnection) pointing to a dedicated local test database.");
            }

            connectionBuilder = new SqlConnectionStringBuilder(baseConn);
            AssertSafeLocalSqlServer(connectionBuilder);

            if (!string.Equals(connectionBuilder.InitialCatalog, DevelopmentDatabaseName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(connectionBuilder.InitialCatalog, IntegrationDatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Integration tests only accept '{DevelopmentDatabaseName}' or '{IntegrationDatabaseName}' as the configured database name.");
            }

            connectionBuilder.InitialCatalog = IntegrationDatabaseName;
        }

        if (string.Equals(connectionBuilder.InitialCatalog, DevelopmentDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to run SQL integration tests against '{DevelopmentDatabaseName}'.");
        }

        ConnectionString = connectionBuilder.ConnectionString;

        lock (_lock)
        {
            if (!_databaseInitialized)
            {
                using var context = CreateContext();
                context.Database.Migrate();
                _databaseInitialized = true;
            }
        }
    }

    public DbContextOptions<ApplicationDbContext> CreateOptions(
        bool enableRetryOnFailure = false,
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(ConnectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                if (enableRetryOnFailure)
                {
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);
                }
            });

        if (interceptors is { Length: > 0 })
        {
            builder.AddInterceptors(interceptors);
        }

        return builder.Options;
    }

    public ApplicationDbContext CreateContext() => CreateContext(enableRetryOnFailure: false);

    public ApplicationDbContext CreateContext(
        bool enableRetryOnFailure,
        params IInterceptor[] interceptors)
    {
        return new ApplicationDbContext(CreateOptions(enableRetryOnFailure, interceptors));
    }

    private static void AssertSafeLocalSqlServer(SqlConnectionStringBuilder builder)
    {
        var dataSource = (builder.DataSource ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new InvalidOperationException("SQL test DataSource must be specified and local.");
        }

        var isLocal = dataSource.Equals(".", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith(@".\", StringComparison.OrdinalIgnoreCase)
            || dataSource.Equals("(local)", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith(@"(local)\", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
            || dataSource.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith(@"localhost\", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("localhost,", StringComparison.OrdinalIgnoreCase)
            || dataSource.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("127.0.0.1,", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith(@"127.0.0.1\", StringComparison.OrdinalIgnoreCase)
            || dataSource.Equals("::1", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("::1,", StringComparison.OrdinalIgnoreCase);

        if (!isLocal)
        {
            throw new InvalidOperationException(
                $"Refusing to run destructive/schema-migrating SQL integration tests against non-local server '{dataSource}'.");
        }
    }

    public void Dispose()
    {
        // Cleanup resources if necessary
    }
}

[CollectionDefinition("DatabaseCollection")]
public class DatabaseCollection : ICollectionFixture<TestDatabaseFixture>
{
}
