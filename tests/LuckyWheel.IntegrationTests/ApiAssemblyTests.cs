using System.Reflection;

namespace LuckyWheel.IntegrationTests;

public class ApiAssemblyTests
{
    [Fact]
    public void Api_Assembly_Should_Be_Loadable()
    {
        // Arrange & Act
        var assembly = Assembly.Load("LuckyWheel.Api");

        // Assert
        Assert.NotNull(assembly);
        Assert.Equal("LuckyWheel.Api", assembly.GetName().Name);
    }
}
