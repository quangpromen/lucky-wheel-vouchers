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
    private const string AcceptedGmailDomain = "gmail.com";
    private const int MaxEmailLength = 320;
    private const int MaxLocalPartLength = 64;

    private static readonly Regex SafeIdempotencyKeyRegex = new(
        @"^[a-zA-Z0-9\-_.]+$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static bool TryNormalizeEmail(string? rawEmail, out string normalizedEmail)
        => TryNormalizeEmailCore(rawEmail, out normalizedEmail, out _);

    public static string NormalizeEmail(string rawEmail)
    {
        if (!TryNormalizeEmailCore(rawEmail, out var normalizedEmail, out var errorMessage))
        {
            throw new ValidationException("email", errorMessage);
        }

        return normalizedEmail;
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

        // 2. Email validation (validate Gmail syntax and accepted domain before normalization)
        if (!TryNormalizeEmailCore(request?.Email, out _, out var emailError))
        {
            errors["email"] = [emailError];
        }

        // 3. AcceptedTerms validation
        if (request?.AcceptedTerms != true)
        {
            errors["acceptedTerms"] = ["You must accept the terms and conditions to spin."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    private static bool TryNormalizeEmailCore(string? rawEmail, out string normalizedEmail, out string errorMessage)
    {
        normalizedEmail = string.Empty;

        if (string.IsNullOrWhiteSpace(rawEmail))
        {
            errorMessage = "Email is required.";
            return false;
        }

        var trimmedEmail = rawEmail.Trim();
        if (trimmedEmail.Length > MaxEmailLength)
        {
            errorMessage = "Email must not exceed 320 characters.";
            return false;
        }

        foreach (var ch in trimmedEmail)
        {
            if (!char.IsAscii(ch) || char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                errorMessage = "Email must be a valid @gmail.com address.";
                return false;
            }
        }

        var lowerEmail = trimmedEmail.ToLowerInvariant();

        var atIndex = lowerEmail.IndexOf('@');
        if (atIndex <= 0 || atIndex != lowerEmail.LastIndexOf('@'))
        {
            errorMessage = "Email must be a valid @gmail.com address.";
            return false;
        }

        var localPart = lowerEmail[..atIndex];
        var domainPart = lowerEmail[(atIndex + 1)..];

        if (!string.Equals(domainPart, AcceptedGmailDomain, StringComparison.Ordinal))
        {
            errorMessage = "Email must be a valid @gmail.com address.";
            return false;
        }

        if (localPart.Length > MaxLocalPartLength)
        {
            errorMessage = "Email must be a valid @gmail.com address.";
            return false;
        }

        var plusIndex = localPart.IndexOf('+');
        if (plusIndex == 0)
        {
            errorMessage = "Email must be a valid @gmail.com address.";
            return false;
        }

        var basePart = plusIndex >= 0 ? localPart[..plusIndex] : localPart;
        var tagPart = plusIndex >= 0 ? localPart[(plusIndex + 1)..] : null;

        if (!IsValidGmailSegments(basePart, allowPlusSeparator: false))
        {
            errorMessage = "Email must be a valid @gmail.com address.";
            return false;
        }

        if (tagPart is not null && !IsValidGmailSegments(tagPart, allowPlusSeparator: true))
        {
            errorMessage = "Email must be a valid @gmail.com address.";
            return false;
        }

        var normalizedLocal = basePart.Replace(".", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(normalizedLocal) || !ContainsAsciiLetterOrDigit(normalizedLocal))
        {
            errorMessage = "Email must be a valid @gmail.com address.";
            return false;
        }

        normalizedEmail = $"{normalizedLocal}@{AcceptedGmailDomain}";
        errorMessage = string.Empty;
        return true;
    }

    private static bool IsValidGmailSegments(string part, bool allowPlusSeparator)
    {
        if (string.IsNullOrEmpty(part))
            return false;

        var segmentLength = 0;
        for (var i = 0; i < part.Length; i++)
        {
            var ch = part[i];
            if (ch == '.' || (allowPlusSeparator && ch == '+'))
            {
                if (segmentLength == 0)
                    return false;

                segmentLength = 0;
                continue;
            }

            if (!IsAllowedGmailLocalChar(ch))
                return false;

            segmentLength++;
        }

        return segmentLength > 0;
    }

    private static bool IsAllowedGmailLocalChar(char ch) =>
        (ch >= 'a' && ch <= 'z')
        || (ch >= '0' && ch <= '9')
        || ch == '_'
        || ch == '-';

    private static bool ContainsAsciiLetterOrDigit(string value)
    {
        foreach (var ch in value)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                return true;
        }

        return false;
    }
}
