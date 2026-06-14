namespace Groovra.Shared.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// Перевіряє, чи має юзер хоча б одну з переданих ролей.
    /// Використання: HttpContext.UserIsInRole(AppRoles.Artist, AppRoles.Admin)
    /// </summary>
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
}