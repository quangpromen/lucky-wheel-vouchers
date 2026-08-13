using System;
using LuckyWheel.Application.Common.Time;

namespace LuckyWheel.Infrastructure.Time;

/// <summary>
/// Production implementation of <see cref="IClock"/> that delegates to
/// <see cref="DateTimeOffset.UtcNow"/>. Registered as a singleton.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc/>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

