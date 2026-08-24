using System;

namespace LuckyWheel.Application.Common.Exceptions;

/// <summary>
/// Thrown when a conflict or concurrency issue occurs (e.g., duplicate data, race condition).
/// Maps to HTTP 409 with errorCode = CONFLICT or a specific conflict code.
/// </summary>
public sealed class ConflictException : Exception
{
    public const string ErrorCode = "CONFLICT";

    public string Code { get; }

    public ConflictException(string message)
        : this(ErrorCode, message)
    {
    }

    public ConflictException(string code, string message)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? ErrorCode : code;
    }
}
