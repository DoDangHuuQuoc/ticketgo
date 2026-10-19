using System.Security.Claims;
namespace EventTicketing.Common;
public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new AppException(401, "UNAUTHORIZED", "Chưa đăng nhập"));
}
