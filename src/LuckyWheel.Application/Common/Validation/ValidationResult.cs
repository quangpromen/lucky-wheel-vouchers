using System;
using System.Collections.Generic;
using System.Linq;
using LuckyWheel.Application.Common.Exceptions;

namespace LuckyWheel.Application.Common.Validation;

/// <summary>
/// Represents the outcome of a validation operation.
/// Does not depend on ASP.NET Core or any third-party library.
/// </summary>
public sealed class ValidationResult
{
    private static readonly ValidationResult _success = new(new Dictionary<string, string[]>());

    private readonly IReadOnlyDictionary<string, string[]> _errors;

    private ValidationResult(IReadOnlyDictionary<string, string[]> errors)
    {
        _errors = errors;
    }

    /// <summary>True when no validation errors were recorded.</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>Field-level errors. Empty when <see cref="IsValid"/> is true.</summary>
    public IReadOnlyDictionary<string, string[]> Errors => _errors;

    // ── Factories ────────────────────────────────────────────────────────────

    /// <summary>Returns a successful (valid) result.</summary>
    public static ValidationResult Success() => _success;

    /// <summary>Returns a failed result with a single field error.</summary>
    public static ValidationResult Failure(string field, string message)
    {
        if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("Field name must not be empty.", nameof(field));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Error message must not be empty.", nameof(message));

        return new ValidationResult(new Dictionary<string, string[]>
        {
            [field] = new[] { message }
        });
    }

    /// <summary>Returns a failed result from multiple field → messages pairs.</summary>
    public static ValidationResult Failure(IDictionary<string, string[]> errors)
    {
        if (errors is null) throw new ArgumentNullException(nameof(errors));
        if (errors.Count == 0) throw new ArgumentException("At least one error must be provided.", nameof(errors));

        return new ValidationResult(new Dictionary<string, string[]>(errors));
    }

    /// <summary>Returns a failed result from a flat list of (field, message) tuples.</summary>
    public static ValidationResult Failure(IEnumerable<(string Field, string Message)> errors)
    {
        if (errors is null) throw new ArgumentNullException(nameof(errors));

        var grouped = errors
            .GroupBy(e => e.Field)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());

        if (grouped.Count == 0)
            throw new ArgumentException("At least one error must be provided.", nameof(errors));

        return new ValidationResult(grouped);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Throws a <see cref="ValidationException"/> if the result is invalid.
    /// No-op when <see cref="IsValid"/> is true.
    /// </summary>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new ValidationException(_errors);
    }
}
