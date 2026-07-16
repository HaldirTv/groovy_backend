
namespace Groovra.Shared.Extensions;

public static class RoleStringExtensions
{
    /// <summary>
    /// Перевіряє, чи містить рядок ролей (через кому) вказану роль.
    /// Працює безпечно: не сплутає "SuperAdmin" з "Admin".
    /// </summary>
    public static bool HasRole(this string rolesString, string targetRole)
    {
        if (string.IsNullOrWhiteSpace(rolesString))
            return false;

        return rolesString
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(r => r.Trim().Equals(targetRole, StringComparison.OrdinalIgnoreCase));
    }
}