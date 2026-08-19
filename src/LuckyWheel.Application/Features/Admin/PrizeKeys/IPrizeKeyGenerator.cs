namespace LuckyWheel.Application.Features.Admin.PrizeKeys;

/// <summary>
/// Generates cryptographically secure, readable prize keys with high entropy (>= 80 bits).
/// </summary>
public interface IPrizeKeyGenerator
{
    string GenerateKey();
}
