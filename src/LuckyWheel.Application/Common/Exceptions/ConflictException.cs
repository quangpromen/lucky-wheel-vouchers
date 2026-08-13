using System;

namespace LuckyWheel.Application.Common.Exceptions;

/// <summary>
/// Thrown when a conflict or concurrency issue occurs (e.g., duplicate data, race condition).
/// Maps to HTTP 409 with errorCode = CONFLICT.
/// </summary>
public sealed class ConflictException : Exception
{
    public const string ErrorCode = "CONFLICT";

    public ConflictException(string message)
        : base(message)
    {
    }
}
