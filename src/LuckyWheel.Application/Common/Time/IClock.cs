using System;

namespace LuckyWheel.Application.Common.Time;

/// <summary>
/// Abstraction over the system clock so that business logic and handlers can
/// obtain the current UTC time without a hard dependency on <see cref="DateTimeOffset.UtcNow"/>,
/// enabling deterministic unit testing.
/// </summary>
public interface IClock
{
    /// <summary>Gets the current date and time expressed as UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
