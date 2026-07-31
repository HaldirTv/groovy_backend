namespace Groovra.Auth.Microservice.DTOS;

public class LoginAuditDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? IpAddress { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ThreatEventDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public bool IsResolved { get; set; }
}

public class OAuthRiskEventDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class SecurityOverviewDto
{
    public int LoginsTotal24h { get; set; }
    public int LoginsFailed24h { get; set; }
    public double FailureRate24h { get; set; }
    public int ThreatsDetected24h { get; set; }
    public double ThreatAvgScore { get; set; }
    public int OAuthRisksDetected24h { get; set; }
    public double OAuthRiskAvgScore { get; set; }
    public int CriticalThreatsCount { get; set; } // Score >= 80
}

public class SecurityStatsDto
{
    public int TotalLoginAttempts { get; set; }
    public int FailedLoginAttempts { get; set; }
    public int TotalThreats { get; set; }
    public int ResolvedThreats { get; set; }
    public int TotalOAuthRisks { get; set; }
    public List<string> TopThreatTypes { get; set; } = new();
    public List<string> TopOAuthProviders { get; set; } = new();
}

public class SecurityPagedResultDto<T> : PagedResultDto<T>
{
}
