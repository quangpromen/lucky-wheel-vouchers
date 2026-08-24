using System;

namespace LuckyWheel.Application.Common.Exceptions;

/// <summary>
/// Thrown when a requested resource is not found.
/// Maps to HTTP 404 with errorCode = NOT_FOUND or a specific not found code.
/// </summary>
public sealed class NotFoundException : Exception
{
    public const string ErrorCode = "NOT_FOUND";

    public string Code { get; }

    public NotFoundException(string message)
        : base(message)
    {
        Code = ErrorCode;
    }

    public NotFoundException(string resourceName, object resourceKey)
        : base($"{resourceName} '{resourceKey}' was not found.")
    {
        Code = ErrorCode;
    }

    public static NotFoundException WithCode(string code, string message)
    {
        return new NotFoundException(code, message, true);
    }

    private NotFoundException(string code, string message, bool _)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? ErrorCode : code;
    }
}
