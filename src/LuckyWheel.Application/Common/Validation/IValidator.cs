namespace LuckyWheel.Application.Common.Validation;

/// <summary>
/// Abstraction for validating a request object.
/// Implement this interface for each command or query that requires input validation.
/// Does not depend on ASP.NET Core or any third-party library.
/// </summary>
/// <typeparam name="TRequest">The request type to validate.</typeparam>
public interface IValidator<in TRequest>
{
    /// <summary>
    /// Validates the given <paramref name="request"/> and returns a <see cref="ValidationResult"/>.
    /// </summary>
    ValidationResult Validate(TRequest request);
}
