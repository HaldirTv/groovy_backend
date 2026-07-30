using System;

namespace Groovra.Billing.Microservice.Models;

public class UserSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string PlanType { get; set; } = "Free"; 
    public int AiMixUsageCount { get; set; } = 0;
    public DateTime? SubscriptionExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
