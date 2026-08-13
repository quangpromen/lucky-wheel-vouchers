using System;

namespace LuckyWheel.Application.Common.Exceptions;

/// <summary>
/// Thrown when the current caller is authenticated but does not have permission to perform the action.
/// Maps to HTTP 403 with errorCode = FORBIDDEN.
/// </summary>
public sealed class ForbiddenException : Exception
{
    public const string ErrorCode = "FORBIDDEN";

    public ForbiddenException(string message)
        : base(message)
    {
    }

    public ForbiddenException()
        : base("Access to the requested resource is forbidden.")
    {
    }
}
