using System.Collections.Generic;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Domain.Common;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Exceptions;

/// <summary>
/// Unit tests verifying exception error codes, messages, and data integrity.
/// </summary>
public sealed class ExceptionTests
{
    // ── ValidationException ───────────────────────────────────────────────────

    [Fact]
    public void ValidationException_ErrorCode_Is_VALIDATION_ERROR()
    {
        Assert.Equal("VALIDATION_ERROR", ValidationException.ErrorCode);
    }

    [Fact]
    public void ValidationException_SingleField_Ctor_StoresError()
    {
        var ex = new ValidationException("email", "Email is required.");

        Assert.Single(ex.Errors);
        Assert.Equal("Email is required.", ex.Errors["email"][0]);
    }

    [Fact]
    public void ValidationException_Dictionary_Ctor_StoresAllErrors()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["email"] = new[] { "Required.", "Must be Gmail." },
            ["name"]  = new[] { "Required." }
        };

        var ex = new ValidationException(errors);

        Assert.Equal(2, ex.Errors.Count);
        Assert.Equal(2, ex.Errors["email"].Length);
    }

    // ── NotFoundException ────────────────────────────────────────────────────

    [Fact]
    public void NotFoundException_ErrorCode_Is_NOT_FOUND()
    {
        Assert.Equal("NOT_FOUND", NotFoundException.ErrorCode);
    }

    [Fact]
    public void NotFoundException_MessageCtor_PreservesMessage()
    {
        var ex = new NotFoundException("Custom not found message.");

        Assert.Equal("Custom not found message.", ex.Message);
    }

    [Fact]
    public void NotFoundException_ResourceCtor_FormatsMessageCorrectly()
    {
        var ex = new NotFoundException("Wheel", "wheel-slug-123");

        Assert.Contains("Wheel", ex.Message);
        Assert.Contains("wheel-slug-123", ex.Message);
    }

    // ── ConflictException ─────────────────────────────────────────────────────

    [Fact]
    public void ConflictException_ErrorCode_Is_CONFLICT()
    {
        Assert.Equal("CONFLICT", ConflictException.ErrorCode);
    }

    [Fact]
    public void ConflictException_PreservesMessage()
    {
        var ex = new ConflictException("Already exists.");

        Assert.Equal("Already exists.", ex.Message);
    }

    // ── ForbiddenException ────────────────────────────────────────────────────

    [Fact]
    public void ForbiddenException_ErrorCode_Is_FORBIDDEN()
    {
        Assert.Equal("FORBIDDEN", ForbiddenException.ErrorCode);
    }

    [Fact]
    public void ForbiddenException_DefaultCtor_HasDefaultMessage()
    {
        var ex = new ForbiddenException();

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    // ── BusinessRuleViolationException ────────────────────────────────────────

    [Fact]
    public void BusinessRuleViolationException_ErrorCode_Is_BUSINESS_RULE_VIOLATION()
    {
        Assert.Equal("BUSINESS_RULE_VIOLATION", BusinessRuleViolationException.ErrorCode);
    }

    [Fact]
    public void BusinessRuleViolationException_StoresRuleCodeAndMessage()
    {
        var ex = new BusinessRuleViolationException("WHEEL_NOT_ACTIVE", "The wheel is not active.");

        Assert.Equal("WHEEL_NOT_ACTIVE", ex.RuleCode);
        Assert.Equal("The wheel is not active.", ex.Message);
    }

    // ── DomainException (existing, from Phase 2) ──────────────────────────────

    [Fact]
    public void DomainException_StoresCodeAndMessage()
    {
        var ex = new DomainException("SPIN_LIMIT_EXCEEDED", "Spin limit exceeded.");

        Assert.Equal("SPIN_LIMIT_EXCEEDED", ex.Code);
        Assert.Equal("Spin limit exceeded.", ex.Message);
    }
}
