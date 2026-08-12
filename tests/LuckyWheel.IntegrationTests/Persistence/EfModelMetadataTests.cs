using LuckyWheel.Domain.Entities;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LuckyWheel.IntegrationTests.Persistence;

public class EfModelMetadataTests
{
    [Fact]
    public void Model_Contains_Expected_Tables_Filters_And_Concurrency_Tokens()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(local);Database=MetadataOnly;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var context = new ApplicationDbContext(options);

        var expectedTables = new Dictionary<Type, string>
        {
            [typeof(Wheel)] = "Wheels",
            [typeof(WheelVersion)] = "WheelVersions",
            [typeof(Prize)] = "Prizes",
            [typeof(WheelVersionPrize)] = "WheelVersionPrizes",
            [typeof(PrizeKey)] = "PrizeKeys",
            [typeof(SpinHistory)] = "SpinHistories",
            [typeof(WinnerLock)] = "WinnerLocks",
            [typeof(PrizeRedemption)] = "PrizeRedemptions",
            [typeof(AuditLog)] = "AuditLogs",
            [typeof(AdminUser)] = "AdminUsers"
        };

        foreach (var expected in expectedTables)
            Assert.Equal(expected.Value, context.Model.FindEntityType(expected.Key)!.GetTableName());

        Assert.Equal("[Status] = 'Active'", FindIndex<WheelVersion>(context, nameof(WheelVersion.WheelId)).GetFilter());
        Assert.Equal("[PrizeKeyId] IS NOT NULL", FindIndex<SpinHistory>(context, nameof(SpinHistory.PrizeKeyId)).GetFilter());
        Assert.Equal("[IsActive] = 1", FindIndex<WinnerLock>(context, nameof(WinnerLock.WheelId), nameof(WinnerLock.EmailNormalized)).GetFilter());

        foreach (var entityType in new[] { typeof(WheelVersion), typeof(Prize), typeof(PrizeKey), typeof(WinnerLock) })
        {
            var rowVersion = context.Model.FindEntityType(entityType)!.FindProperty("RowVersion")!;
            Assert.True(rowVersion.IsConcurrencyToken);
            Assert.False(rowVersion.IsNullable);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
        }

        Assert.All(context.Model.GetEntityTypes().SelectMany(x => x.GetForeignKeys()),
            foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    private static IIndex FindIndex<TEntity>(ApplicationDbContext context, params string[] propertyNames)
    {
        return context.Model.FindEntityType(typeof(TEntity))!.GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }
}
