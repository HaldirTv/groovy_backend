namespace Groovra.Auth.Microservice.DTOS;

public class AdminUserListItemDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public bool IsSuspended { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PagedResultDto<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
}

public class AdminArtistApplicationDto
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? SubmittedAt { get; set; }
}

public class AdminRoleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int MemberCount { get; set; }
}

public class DashboardStatsDto
{
    public TotalUsersStatsDto TotalUsers { get; set; } = new();
    public MonthlyRevenueStatsDto MonthlyRevenue { get; set; } = new();
    public ActiveSubscriptionsStatsDto ActiveSubscriptions { get; set; } = new();
    public AiGeneratedTracksStatsDto AiGeneratedTracks { get; set; } = new();
    public PendingReportsStatsDto PendingReports { get; set; } = new();
    public GrowthRateStatsDto GrowthRate { get; set; } = new();
}

public class TotalUsersStatsDto
{
    public string Value { get; set; } = string.Empty;
    public double ChangePercent { get; set; }
    public double ProgressPercent { get; set; }
    public string TargetLabel { get; set; } = string.Empty;
}

public class MonthlyRevenueStatsDto
{
    public string Value { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;
    public List<RevenueMonthDto> Months { get; set; } = new();
}

public class RevenueMonthDto
{
    public string Label { get; set; } = string.Empty;
    public double Value { get; set; }
    public bool Active { get; set; }
}

public class ActiveSubscriptionsStatsDto
{
    public string Value { get; set; } = string.Empty;
    public string RatioLabel { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
}

public class AiGeneratedTracksStatsDto
{
    public string Value { get; set; } = string.Empty;
}

public class PendingReportsStatsDto
{
    public string Value { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
}

public class GrowthRateStatsDto
{
    public string AvgPerDayLabel { get; set; } = string.Empty;
    public string RetentionLabel { get; set; } = string.Empty;
}

public class ActivityFeedItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Tone { get; set; } = string.Empty;
    public string IconType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class RoleCapabilityDto
{
    public string Feature { get; set; } = string.Empty;
    public bool CanView { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
}

public class RoleCapabilitiesDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<RoleCapabilityDto> Permissions { get; set; } = new();
}

public class CreateUserRequestDto
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class CreateRoleRequestDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class BulkActionRequestDto
{
    public List<Guid> UserIds { get; set; } = new();
}
