
namespace Groovra.Shared.Extensions;

public static class RoleStringExtensions
{
    public static bool HasRole(this string rolesString, string targetRole)
    {
        if (string.IsNullOrWhiteSpace(rolesString))
            return false;

        return rolesString
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(r => r.Trim().Equals(targetRole, StringComparison.OrdinalIgnoreCase));
    }
}