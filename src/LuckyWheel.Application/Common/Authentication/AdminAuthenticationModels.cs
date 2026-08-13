using System;

namespace LuckyWheel.Application.Common.Authentication;

public sealed record AdminIdentity(Guid Id, string Username, string DisplayName);

public sealed record GeneratedAccessToken(string AccessToken, DateTimeOffset ExpiresAtUtc);

public sealed record AdminLoginResult(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    AdminIdentity Admin);
