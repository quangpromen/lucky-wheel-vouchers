using System;

namespace LuckyWheel.Application.Common.Exceptions;

/// <summary>
/// Thrown when a requested resource is not found.
/// Maps to HTTP 404 with errorCode = NOT_FOUND.
/// </summary>
public sealed class NotFoundException : Exception
{
    public const string ErrorCode = "NOT_FOUND";

    public NotFoundException(string message)
        : base(message)
    {
    }

    public NotFoundException(string resourceName, object resourceKey)
        : base($"{resourceName} '{resourceKey}' was not found.")
    {
    }
}
