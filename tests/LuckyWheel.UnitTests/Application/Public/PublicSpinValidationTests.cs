using System.Collections.Generic;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Features.Public;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Public;

public sealed class PublicSpinValidationTests
{
    [Theory]
    [InlineData("user@gmail.com", "user@gmail.com")]
    [InlineData("  User@Gmail.COM  ", "user@gmail.com")]
    [InlineData("quang.dev@gmail.com", "quang.dev@gmail.com")]
    [InlineData("quang.dev+alias123@gmail.com", "quang.dev+alias123@gmail.com")]
    public void NormalizeEmail_PreservesDotsAndAliases_ConvertsToLowercaseAndTrims(string raw, string expected)
    {
        var normalized = PublicSpinValidation.NormalizeEmail(raw);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("user@gmail.com", "valid-idempotency-key-123", true)]
    [InlineData("user.test+spin@gmail.com", "550e8400-e29b-41d4-a716-446655440000", true)]
    [InlineData("player_99@gmail.com", "abc_DEF-123.xyz", true)]
    public void ValidateSpinRequest_ValidInputs_PassesValidation(string email, string idempotencyKey, bool terms)
    {
        var request = new SpinRequest(email, terms);

        // Act & Assert - does not throw
        PublicSpinValidation.ValidateSpinRequest(idempotencyKey, request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSpinRequest_MissingIdempotencyKey_ThrowsValidationException(string? idempotencyKey)
    {
        var request = new SpinRequest("user@gmail.com", true);

        var ex = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest(idempotencyKey, request));

        Assert.True(ex.Errors.ContainsKey("idempotencyKey"));
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("::1")]
    [InlineData("user@gmail.com")]
    [InlineData("invalid key with spaces")]
    [InlineData("key<script>alert(1)</script>")]
    [InlineData("key;drop table spin;")]
    public void ValidateSpinRequest_InvalidIdempotencyKeyFormat_ThrowsValidationException(string invalidKey)
    {
        var request = new SpinRequest("user@gmail.com", true);

        var ex = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest(invalidKey, request));

        Assert.True(ex.Errors.ContainsKey("idempotencyKey"));
    }

    [Fact]
    public void ValidateSpinRequest_TooLongIdempotencyKey_ThrowsValidationException()
    {
        var tooLongKey = new string('a', 101);
        var request = new SpinRequest("user@gmail.com", true);

        var ex = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest(tooLongKey, request));

        Assert.True(ex.Errors.ContainsKey("idempotencyKey"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user@yahoo.com")]
    [InlineData("user@outlook.com")]
    [InlineData("user@gmail.com.vn")]
    [InlineData("invalid-email")]
    [InlineData("@gmail.com")]
    public void ValidateSpinRequest_InvalidEmail_ThrowsValidationException(string? email)
    {
        var request = new SpinRequest(email, true);

        var ex = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest("safe-key-123", request));

        Assert.True(ex.Errors.ContainsKey("email"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void ValidateSpinRequest_TermsNotAccepted_ThrowsValidationException(bool? acceptedTerms)
    {
        var request = new SpinRequest("user@gmail.com", acceptedTerms);

        var ex = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest("safe-key-123", request));

        Assert.True(ex.Errors.ContainsKey("acceptedTerms"));
    }
}
