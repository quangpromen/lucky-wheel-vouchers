using System;
using System.Collections.Generic;

namespace LuckyWheel.Application.Common.Exceptions;

/// <summary>
/// Thrown when one or more request validation errors occur.
/// Does not depend on ASP.NET Core.
/// </summary>
public sealed class ValidationException : Exception
{
    public const string ErrorCode = "VALIDATION_ERROR";

    /// <summary>Field-level validation errors: field name → array of messages.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
    }

    /// <summary>Convenience constructor for a single field error.</summary>
    public ValidationException(string field, string message)
        : this(new Dictionary<string, string[]>
        {
            [field] = new[] { message }
        })
    {
    }
}
