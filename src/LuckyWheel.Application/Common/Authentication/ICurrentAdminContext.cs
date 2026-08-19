using System;

namespace LuckyWheel.Application.Common.Authentication;

/// <summary>
/// Abstraction to access the current authenticated admin user identity
/// without coupling the Application layer to ASP.NET Core or HttpContext.
/// </summary>
public interface ICurrentAdminContext
{
    Guid? AdminId { get; }
    bool IsAuthenticated { get; }
}
