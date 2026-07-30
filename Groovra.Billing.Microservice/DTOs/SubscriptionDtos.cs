using System;

namespace Groovra.Billing.Microservice.DTOs;

public class SubscriptionStatusDto
{
    public string PlanType { get; set; } = "Free";
    public int AiMixUsageCount { get; set; } = 0;
    public int AiMixLimit { get; set; } = 3;
    public int RemainingAiMixes { get; set; } = 3;
    public bool IsActivePremium { get; set; } = false;
    public DateTime? SubscriptionExpiresAt { get; set; }
}

public class UpgradeSubscriptionDto
{
    public string PlanType { get; set; } = "Premium"; 
    public int DurationMonths { get; set; } = 1;
}

public class CheckAiMixResponseDto
{
    public bool Allowed { get; set; }
    public int RemainingCount { get; set; }
    public string Message { get; set; } = string.Empty;
}
