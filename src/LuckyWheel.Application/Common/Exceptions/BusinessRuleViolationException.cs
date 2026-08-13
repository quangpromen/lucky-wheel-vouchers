using System;

namespace LuckyWheel.Application.Common.Exceptions;

/// <summary>
/// Thrown when an Application-layer operation detects a business rule violation
/// that does not originate from a Domain entity.
/// Domain-level violations should use <see cref="LuckyWheel.Domain.Common.DomainException"/> instead.
/// Maps to HTTP 400 with errorCode = BUSINESS_RULE_VIOLATION.
/// </summary>
public sealed class BusinessRuleViolationException : Exception
{
    public const string ErrorCode = "BUSINESS_RULE_VIOLATION";

    /// <summary>Stable machine-readable rule code (e.g. "WHEEL_NOT_ACTIVE").</summary>
    public string RuleCode { get; }

    public BusinessRuleViolationException(string ruleCode, string message)
        : base(message)
    {
        RuleCode = ruleCode ?? throw new ArgumentNullException(nameof(ruleCode));
    }
}
