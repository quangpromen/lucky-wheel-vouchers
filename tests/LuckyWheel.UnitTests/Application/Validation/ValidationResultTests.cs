using System.Collections.Generic;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Validation;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Validation;

/// <summary>
/// Unit tests for <see cref="ValidationResult"/>.
/// Covers: valid result, single-field error, multi-error same field, multi-field, ThrowIfInvalid.
/// </summary>
public sealed class ValidationResultTests
{
    // ── Success factory ───────────────────────────────────────────────────────

    [Fact]
    public void Success_Returns_ValidResult()
    {
        var result = ValidationResult.Success();

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Success_ThrowIfInvalid_DoesNotThrow()
    {
        var result = ValidationResult.Success();

        // Should not throw
        result.ThrowIfInvalid();
    }

    // ── Single field error ────────────────────────────────────────────────────

    [Fact]
    public void Failure_SingleField_Returns_InvalidResult_WithOneError()
    {
        var result = ValidationResult.Failure("email", "Email is required.");

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.True(result.Errors.ContainsKey("email"));
        Assert.Single(result.Errors["email"]);
        Assert.Equal("Email is required.", result.Errors["email"][0]);
    }

    // ── Multiple errors on same field (via tuple factory) ─────────────────────

    [Fact]
    public void Failure_Tuples_MultipleErrorsSameField_AreGrouped()
    {
        var errors = new (string Field, string Message)[]
        {
            ("email", "Email is required."),
            ("email", "Email must be a valid Gmail address.")
        };

        var result = ValidationResult.Failure(errors);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal(2, result.Errors["email"].Length);
        Assert.Contains("Email is required.", result.Errors["email"]);
        Assert.Contains("Email must be a valid Gmail address.", result.Errors["email"]);
    }

    // ── Multiple fields with errors ───────────────────────────────────────────

    [Fact]
    public void Failure_Dictionary_MultipleFields_PreservesAllErrors()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["email"] = new[] { "Email is required." },
            ["name"]  = new[] { "Name is required.", "Name must be at least 2 characters." }
        };

        var result = ValidationResult.Failure(errors);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Single(result.Errors["email"]);
        Assert.Equal(2, result.Errors["name"].Length);
    }

    // ── ThrowIfInvalid converts to ValidationException ─────────────────────────

    [Fact]
    public void ThrowIfInvalid_WhenInvalid_Throws_ValidationException()
    {
        var result = ValidationResult.Failure("code", "Code is required.");

        var ex = Assert.Throws<ValidationException>(result.ThrowIfInvalid);

        Assert.NotNull(ex);
        Assert.NotNull(ex.Errors);
    }

    [Fact]
    public void ThrowIfInvalid_PreservesErrors_InValidationException()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["email"] = new[] { "Email is required.", "Must be Gmail." },
            ["slug"]  = new[] { "Slug is required." }
        };
        var result = ValidationResult.Failure(errors);

        var ex = Assert.Throws<ValidationException>(result.ThrowIfInvalid);

        Assert.Equal(2, ex.Errors.Count);
        Assert.Equal(2, ex.Errors["email"].Length);
        Assert.Single(ex.Errors["slug"]);
    }

    [Fact]
    public void ThrowIfInvalid_ValidationException_HasCorrectErrorCode()
    {
        var result = ValidationResult.Failure("x", "msg");

        var ex = Assert.Throws<ValidationException>(result.ThrowIfInvalid);

        Assert.Equal("VALIDATION_ERROR", ValidationException.ErrorCode);
    }

    // ── Guard clauses ─────────────────────────────────────────────────────────

    [Fact]
    public void Failure_NullField_Throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ValidationResult.Failure(null!, "msg"));
    }

    [Fact]
    public void Failure_EmptyErrors_Dictionary_Throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ValidationResult.Failure(new Dictionary<string, string[]>()));
    }
}
