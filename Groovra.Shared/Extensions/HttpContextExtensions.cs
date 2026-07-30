using System.Net;
using Groovra.Shared.Constants;

namespace Groovra.Shared.Extensions;

public static class HttpContextExtensions
{
    public static bool UserIsInRole(this HttpContext context, params string[] allowedRoles)
    {
        var rolesHeader = context.Request.Headers["X-User-Role"].ToString();
        if (string.IsNullOrWhiteSpace(rolesHeader))
            return false;

        var userRoles = rolesHeader
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allowedRoles.Any(role => userRoles.Contains(role));
    }
    public static bool TryGetUserId(this HttpContext context, out Guid userId)
    {
        return Guid.TryParse(context.Request.Headers["X-User-Id"].ToString(), out userId);
    }

    public static string GetUserName(this HttpContext context)
    {
        var encodedName = context.Request.Headers["X-User-Name"].ToString();
        return WebUtility.UrlDecode(encodedName) ?? string.Empty;
    }
}