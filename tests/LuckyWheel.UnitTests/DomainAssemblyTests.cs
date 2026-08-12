using System.Reflection;

namespace LuckyWheel.UnitTests;

public class DomainAssemblyTests
{
    [Fact]
    public void Domain_Assembly_Should_Be_Loadable()
    {
        // Arrange & Act
        var assembly = Assembly.Load("LuckyWheel.Domain");

        // Assert
        Assert.NotNull(assembly);
        Assert.Equal("LuckyWheel.Domain", assembly.GetName().Name);
    }
}
