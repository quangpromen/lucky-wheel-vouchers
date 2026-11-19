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
    [InlineData("quang.dev@gmail.com", "quangdev@gmail.com")]
    [InlineData("Quang.Dev+campaign@gmail.com", "quangdev@gmail.com")]
    [InlineData("  q.u.a.n.g.d.e.v+promo.2026-1_a@GMAIL.COM  ", "quangdev@gmail.com")]
    [InlineData("a.b.c@gmail.com", "abc@gmail.com")]
    [InlineData("123.456+test@gmail.com", "123456@gmail.com")]
    [InlineData("user+a+b@gmail.com", "user@gmail.com")]
    [InlineData("User.Name+Tag.1+Tag.2@Gmail.Com", "username@gmail.com")]
    [InlineData("player_99@gmail.com", "player_99@gmail.com")]
    [InlineData("p.l.a.y.e.r_9.9+spin@gmail.com", "player_99@gmail.com")]
    [InlineData("john-doe+test@gmail.com", "john-doe@gmail.com")]
    public void NormalizeEmail_ValidGmailVariants_NormalizesToCanonicalGmail(string raw, string expected)
    {
        var normalized = PublicSpinValidation.NormalizeEmail(raw);
        Assert.Equal(expected, normalized);
        Assert.True(PublicSpinValidation.TryNormalizeEmail(raw, out var tryNormalized));
        Assert.Equal(expected, tryNormalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user@yahoo.com")]
    [InlineData("user@outlook.com")]
    [InlineData("user@gmail.com.vn")]
    [InlineData("user@googlemail.com")]
    [InlineData("user@sub.gmail.com")]
    [InlineData("user@gmail.co")]
    [InlineData("user@gmail.com.")]
    [InlineData("invalid-email")]
    [InlineData("@gmail.com")]
    [InlineData("user@")]
    [InlineData("user@@gmail.com")]
    [InlineData("a@b@gmail.com")]
    [InlineData(".user@gmail.com")]
    [InlineData("user.@gmail.com")]
    [InlineData("user..name@gmail.com")]
    [InlineData(".@gmail.com")]
    [InlineData("..@gmail.com")]
    [InlineData("...@gmail.com")]
    [InlineData("+tag@gmail.com")]
    [InlineData(".+tag@gmail.com")]
    [InlineData("user+@gmail.com")]
    [InlineData("user.+tag@gmail.com")]
    [InlineData("user+.tag@gmail.com")]
    [InlineData("user+tag.@gmail.com")]
    [InlineData("user+a..b@gmail.com")]
    [InlineData("user++tag@gmail.com")]
    [InlineData("user+tag+@gmail.com")]
    [InlineData("user name@gmail.com")]
    [InlineData("user\t@gmail.com")]
    [InlineData("user@ gmail.com")]
    [InlineData("user!@gmail.com")]
    [InlineData("user#1@gmail.com")]
    [InlineData("user<script>@gmail.com")]
    [InlineData("\"user\"@gmail.com")]
    [InlineData("Bob <user@gmail.com>")]
    [InlineData("nguyễn@gmail.com")]
    [InlineData("\u212A@gmail.com")]
    [InlineData("\u212A.user@gmail.com")]
    [InlineData("user\u200B@gmail.com")]
    [InlineData("user\u00A0name@gmail.com")]
    [InlineData("user@gmail.com\0")]
    [InlineData("user@.gmail.com")]
    [InlineData("user@gmail..com")]
    [InlineData("_@gmail.com")]
    [InlineData("-@gmail.com")]
    [InlineData("._@gmail.com")]
    public void NormalizeEmail_InvalidGmail_ThrowsValidationExceptionBeforeNormalization(string? raw)
    {
        var ex = Assert.Throws<ValidationException>(() => PublicSpinValidation.NormalizeEmail(raw!));
        Assert.True(ex.Errors.ContainsKey("email"));
        Assert.False(PublicSpinValidation.TryNormalizeEmail(raw, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void NormalizeEmail_TooLongLocalPartOrEmail_ThrowsValidationException()
    {
        var tooLongLocal = new string('a', 65) + "@gmail.com";
        var tooLongEmail = new string('a', 311) + "@gmail.com";

        Assert.Throws<ValidationException>(() => PublicSpinValidation.NormalizeEmail(tooLongLocal));
        var ex = Assert.Throws<ValidationException>(() => PublicSpinValidation.NormalizeEmail(tooLongEmail));
        Assert.Contains("320", ex.Errors["email"][0]);
    }

    [Theory]
    [InlineData("user@gmail.com", "valid-idempotency-key-123", true)]
    [InlineData("user.test+spin@gmail.com", "550e8400-e29b-41d4-a716-446655440000", true)]
    [InlineData("  Quang.Dev+campaign@GMAIL.COM  ", "key-variant-01", true)]
    [InlineData("q.u.a.n.g.d.e.v@gmail.com", "key-variant-02", true)]
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
    [InlineData("user@googlemail.com")]
    [InlineData("user@sub.gmail.com")]
    [InlineData("user@gmail.co")]
    [InlineData("invalid-email")]
    [InlineData("@gmail.com")]
    [InlineData(".user@gmail.com")]
    [InlineData("user.@gmail.com")]
    [InlineData("user..name@gmail.com")]
    [InlineData(".@gmail.com")]
    [InlineData("...@gmail.com")]
    [InlineData("+tag@gmail.com")]
    [InlineData(".+tag@gmail.com")]
    [InlineData("user+@gmail.com")]
    [InlineData("user.+tag@gmail.com")]
    [InlineData("user+.tag@gmail.com")]
    [InlineData("user+tag.@gmail.com")]
    [InlineData("user+a..b@gmail.com")]
    [InlineData("user++tag@gmail.com")]
    [InlineData("user name@gmail.com")]
    [InlineData("user<script>@gmail.com")]
    [InlineData("nguyễn@gmail.com")]
    [InlineData("\u212A@gmail.com")]
    [InlineData("user\u200B@gmail.com")]
    [InlineData("user@gmail.com\0")]
    public void ValidateSpinRequest_InvalidEmail_ThrowsValidationException(string? email)
    {
        var request = new SpinRequest(email, true);

        var ex = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest("safe-key-123", request));

        Assert.True(ex.Errors.ContainsKey("email"));
    }

    [Fact]
    public void ValidateSpinRequest_TooLongEmailOrLocalPart_ThrowsValidationException()
    {
        var tooLongLocal = new SpinRequest(new string('a', 65) + "@gmail.com", true);
        var tooLongEmail = new SpinRequest(new string('a', 311) + "@gmail.com", true);

        var exLocal = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest("safe-key-123", tooLongLocal));
        var exEmail = Assert.Throws<ValidationException>(() =>
            PublicSpinValidation.ValidateSpinRequest("safe-key-123", tooLongEmail));

        Assert.True(exLocal.Errors.ContainsKey("email"));
        Assert.True(exEmail.Errors.ContainsKey("email"));
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
