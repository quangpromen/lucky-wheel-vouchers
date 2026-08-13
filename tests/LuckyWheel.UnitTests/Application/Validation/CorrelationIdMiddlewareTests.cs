using LuckyWheel.Api.Middleware;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Validation;

/// <summary>
/// Unit tests for <see cref="CorrelationIdMiddleware.IsValidCorrelationId"/> static helper.
/// </summary>
public sealed class CorrelationIdMiddlewareTests
{
    [Theory]
    [InlineData("abc123")]
    [InlineData("my-trace-id")]
    [InlineData("trace_001")]
    [InlineData("a.b.c")]
    [InlineData("A1B2C3")]
    public void IsValidCorrelationId_ValidValues_ReturnsTrue(string value)
    {
        Assert.True(CorrelationIdMiddleware.IsValidCorrelationId(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidCorrelationId_NullOrWhitespace_ReturnsFalse(string? value)
    {
        Assert.False(CorrelationIdMiddleware.IsValidCorrelationId(value!));
    }

    [Fact]
    public void IsValidCorrelationId_Exactly64Chars_ReturnsTrue()
    {
        var value = new string('a', 64);
        Assert.True(CorrelationIdMiddleware.IsValidCorrelationId(value));
    }

    [Fact]
    public void IsValidCorrelationId_65Chars_ReturnsFalse()
    {
        var value = new string('a', 65);
        Assert.False(CorrelationIdMiddleware.IsValidCorrelationId(value));
    }

    [Theory]
    [InlineData("trace id")]      // space
    [InlineData("trace<id>")]     // angle brackets
    [InlineData("trace;id")]      // semicolon
    [InlineData("trace\nid")]     // newline
    [InlineData("trace\0id")]     // null byte
    public void IsValidCorrelationId_UnsafeCharacters_ReturnsFalse(string value)
    {
        Assert.False(CorrelationIdMiddleware.IsValidCorrelationId(value));
    }
}
