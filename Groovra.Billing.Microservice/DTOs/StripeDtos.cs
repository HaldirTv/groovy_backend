using System;

namespace Groovra.Billing.Microservice.DTOs;

public class CreateStripeCheckoutDto
{
    public string PlanType { get; set; } = "Premium"; 
    public int DurationMonths { get; set; } = 1; 
    public string BillingCycle { get; set; } = "monthly"; 
    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
}

public class StripeCheckoutResponseDto
{
    public string SessionId { get; set; } = string.Empty;
    public string CheckoutUrl { get; set; } = string.Empty;
    public string PublishableKey { get; set; } = string.Empty;
    public bool IsSandboxMode { get; set; } = true;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string PlanType { get; set; } = "Premium";
    public int DurationMonths { get; set; } = 1;
}

public class ConfirmStripeSandboxDto
{
    public string SessionId { get; set; } = string.Empty;
}

public class PaymentRecordDto
{
    public Guid Id { get; set; }
    public string StripeSessionId { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
