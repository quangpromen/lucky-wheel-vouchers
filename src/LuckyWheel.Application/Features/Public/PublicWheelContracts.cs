using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Validation;
using LuckyWheel.Domain.Entities;

namespace LuckyWheel.Application.Features.Public;

public sealed record PublicWheelDto(
    Guid WheelId,
    string Name,
    string Slug,
    Guid VersionId,
    int VersionNumber,
    IReadOnlyList<PublicWheelEntryDto> Entries);

public sealed record PublicWheelEntryDto(
    Guid Id,
    int DisplayOrder,
    string Label,
    string? Color,
    string? ImageUrl,
    bool IsNoPrize);

public sealed record SpinRequest(
    string? Email,
    bool? AcceptedTerms);

public sealed record SpinResultDto(
    Guid SpinId,
    string ReceiptToken,
    string Result,
    Guid EntryId,
    int DisplayOrder,
    string Message,
    SpinPrizeDto? Prize,
    DateTime CompletedAtUtc);

public sealed record SpinPrizeDto(
    Guid Id,
    string Name,
    string? Description,
    string? Key,
    DateTime? KeyExpiresAtUtc);

public interface IPublicWheelService
{
    Task<PublicWheelDto> GetActiveWheelBySlugAsync(string slug, CancellationToken ct = default);
    Task<SpinResultDto> SpinAsync(string slug, string? idempotencyKeyHeader, SpinRequest request, CancellationToken ct = default);
}

public interface IWeightedPrizeSelector
{
    WheelVersionPrize Select(IReadOnlyList<WheelVersionPrize> eligibleCandidates);
}

public interface IRandomNumberProvider
{
    int GetInt32(int fromInclusive, int toExclusive);
    long GetInt64(long fromInclusive, long toExclusive);
    byte[] GetBytes(int count);
}

public static class PublicSpinValidation
{
    private static readonly Regex SafeIdempotencyKeyRegex = new(
        @"^[a-zA-Z0-9\-_.]+$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex EmailFormatRegex = new(
        @"^[^@\s]+@gmail\.com$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public static string NormalizeEmail(string rawEmail)
    {
        return rawEmail.Trim().ToLowerInvariant();
    }

    public static void ValidateSpinRequest(string? idempotencyKey, SpinRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // 1. Idempotency-Key validation
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            errors["idempotencyKey"] = ["Idempotency-Key header is required."];
        }
        else
        {
            var trimmedKey = idempotencyKey.Trim();
            if (trimmedKey.Length > 100)
            {
                errors["idempotencyKey"] = ["Idempotency-Key must not exceed 100 characters."];
            }
            else if (!SafeIdempotencyKeyRegex.IsMatch(trimmedKey))
            {
                errors["idempotencyKey"] = ["Idempotency-Key contains invalid characters. Only alphanumeric, hyphen, underscore, and period are allowed."];
            }
            else if (IPAddress.TryParse(trimmedKey, out _) || trimmedKey.Contains('@'))
            {
                errors["idempotencyKey"] = ["Idempotency-Key must not be an IP address or email address."];
            }
        }

        // 2. Email validation
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors["email"] = ["Email is required."];
        }
        else
        {
            var trimmedEmail = request.Email.Trim();
            if (trimmedEmail.Length > 320)
            {
                errors["email"] = ["Email must not exceed 320 characters."];
            }
            else if (!EmailFormatRegex.IsMatch(trimmedEmail))
            {
                errors["email"] = ["Email must be a valid @gmail.com address."];
            }
        }

        // 3. AcceptedTerms validation
        if (request.AcceptedTerms != true)
        {
            errors["acceptedTerms"] = ["You must accept the terms and conditions to spin."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
