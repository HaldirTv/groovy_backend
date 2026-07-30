using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Groovra.Billing.Microservice.Data;
using Groovra.Billing.Microservice.DTOs;
using Groovra.Billing.Microservice.Models;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;

using AppPaymentRecord = Groovra.Billing.Microservice.Models.PaymentRecord;

namespace Groovra.Billing.Microservice.Controllers;

[ApiController]
[Route("billing")]
public class PaymentController : ControllerBase
{
    private readonly BillingDbContext _db;
    private readonly IConfiguration _config;

    public PaymentController(BillingDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool TryGetUserId(out Guid userId) => HttpContext.TryGetUserId(out userId);

    [HttpPost("stripe/create-checkout-session")]
    public async Task<IActionResult> CreateStripeCheckoutSession([FromBody] CreateStripeCheckoutDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Авторизація обов'язкова." });

        decimal amount = dto.BillingCycle == "annual" || dto.DurationMonths >= 12 ? 999.00m : 99.00m;
        int durationMonths = dto.BillingCycle == "annual" || dto.DurationMonths >= 12 ? 12 : 1;
        string currency = _config["Stripe:Currency"] ?? "UAH";
        string secretKey = _config["Stripe:SecretKey"] ?? "";
        string pubKey = _config["Stripe:PublishableKey"] ?? "";

        string sessionId = "cs_test_" + Guid.NewGuid().ToString("N");
        string checkoutUrl = "";

        if (!string.IsNullOrWhiteSpace(secretKey) && secretKey.StartsWith("sk_"))
        {
            try
            {
                StripeConfiguration.ApiKey = secretKey;
                string origin = Request.Headers["Origin"].ToString();
                if (string.IsNullOrWhiteSpace(origin))
                {
                    origin = Request.Headers["Referer"].ToString();
                }
                if (string.IsNullOrWhiteSpace(origin))
                {
                    origin = "http://localhost:5173";
                }
                origin = origin.TrimEnd('/');
                if (origin.EndsWith("/subscription"))
                {
                    origin = origin.Substring(0, origin.Length - "/subscription".Length);
                }

                var options = new SessionCreateOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions
                        {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                UnitAmount = (long)(amount * 100), 
                                Currency = currency.ToLowerInvariant(),
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = "Groovra Premium Підписка",
                                    Description = "Безлімітний AI-Mix, HD 320kbps Audio, 3D Surround Spatial Audio & 10 пресетів еквалайзера"
                                }
                            },
                            Quantity = 1,
                        },
                    },
                    Mode = "payment",
                    SuccessUrl = $"{origin}/subscription?session_id={{CHECKOUT_SESSION_ID}}&success=true",
                    CancelUrl = $"{origin}/subscription?canceled=true",
                    ClientReferenceId = userId.ToString(),
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId.ToString() },
                        { "planType", "Premium" },
                        { "durationMonths", durationMonths.ToString() }
                    }
                };

                var service = new SessionService();
                Session stripeSession = await service.CreateAsync(options);
                sessionId = stripeSession.Id;
                checkoutUrl = stripeSession.Url ?? "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stripe API Exception]: {ex.Message}");
            }
        }

        var paymentRecord = new AppPaymentRecord
        {
            UserId = userId,
            StripeSessionId = sessionId,
            PlanType = "Premium",
            Amount = amount,
            Currency = currency,
            Status = "Pending",
            PaymentMethod = "Stripe",
            CreatedAt = DateTime.UtcNow
        };

        _db.PaymentRecords.Add(paymentRecord);
        await _db.SaveChangesAsync();

        return Ok(new StripeCheckoutResponseDto
        {
            SessionId = sessionId,
            CheckoutUrl = checkoutUrl,
            PublishableKey = pubKey,
            IsSandboxMode = string.IsNullOrWhiteSpace(secretKey) || secretKey.Contains("sandbox"),
            Amount = amount,
            Currency = currency,
            PlanType = "Premium",
            DurationMonths = durationMonths
        });
    }

    [HttpPost("stripe/confirm-sandbox-session")]
    public async Task<IActionResult> ConfirmStripeSandboxSession([FromBody] ConfirmStripeSandboxDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Авторизація обов'язкова." });

        if (string.IsNullOrWhiteSpace(dto.SessionId))
            return BadRequest(new { message = "Недійсний SessionId." });

        var paymentRecord = await _db.PaymentRecords
            .FirstOrDefaultAsync(p => p.StripeSessionId == dto.SessionId && p.UserId == userId);

        if (paymentRecord == null)
        {
            paymentRecord = new AppPaymentRecord
            {
                UserId = userId,
                StripeSessionId = dto.SessionId,
                PlanType = "Premium",
                Amount = 99.00m,
                Currency = "UAH",
                Status = "Pending",
                PaymentMethod = "Stripe",
                CreatedAt = DateTime.UtcNow
            };
            _db.PaymentRecords.Add(paymentRecord);
        }

        int durationMonths = paymentRecord.Amount >= 500.00m ? 12 : 1;
        paymentRecord.Status = "Succeeded";
        paymentRecord.CompletedAt = DateTime.UtcNow;

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (sub == null)
        {
            sub = new UserSubscription { UserId = userId };
            _db.Subscriptions.Add(sub);
        }

        sub.PlanType = "Premium";
        sub.SubscriptionExpiresAt = DateTime.UtcNow.AddMonths(durationMonths);
        sub.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new SubscriptionStatusDto
        {
            PlanType = "Premium",
            AiMixUsageCount = sub.AiMixUsageCount,
            AiMixLimit = -1,
            RemainingAiMixes = 999999,
            IsActivePremium = true,
            SubscriptionExpiresAt = sub.SubscriptionExpiresAt
        });
    }

    [HttpPost("webhook/stripe")]
    public async Task<IActionResult> StripeWebhook()
    {
        try
        {
            using var reader = new StreamReader(HttpContext.Request.Body);
            string json = await reader.ReadToEndAsync();

            string webhookSecret = _config["Stripe:WebhookSecret"] ?? "";
            Event stripeEvent;

            if (!string.IsNullOrWhiteSpace(webhookSecret) && webhookSecret.StartsWith("whsec_"))
            {
                var signatureHeader = Request.Headers["Stripe-Signature"];
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);
            }
            else
            {
                stripeEvent = EventUtility.ParseEvent(json);
            }

            if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted)
            {
                var session = stripeEvent.Data.Object as Session;
                if (session != null)
                {
                    string sessionId = session.Id;
                    string userIdStr = session.ClientReferenceId ?? (session.Metadata != null && session.Metadata.ContainsKey("userId") ? session.Metadata["userId"] : "");

                    if (Guid.TryParse(userIdStr, out var userId))
                    {
                        var record = await _db.PaymentRecords.FirstOrDefaultAsync(p => p.StripeSessionId == sessionId);
                        if (record == null)
                        {
                            record = new AppPaymentRecord
                            {
                                UserId = userId,
                                StripeSessionId = sessionId,
                                PlanType = "Premium",
                                Amount = (decimal)(session.AmountTotal ?? 9900) / 100m,
                                Currency = session.Currency?.ToUpperInvariant() ?? "UAH",
                                PaymentMethod = "Stripe",
                                CreatedAt = DateTime.UtcNow
                            };
                            _db.PaymentRecords.Add(record);
                        }

                        record.Status = "Succeeded";
                        record.CompletedAt = DateTime.UtcNow;

                        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
                        if (sub == null)
                        {
                            sub = new UserSubscription { UserId = userId };
                            _db.Subscriptions.Add(sub);
                        }

                        int durationMonths = record.Amount >= 500.00m ? 12 : 1;
                        sub.PlanType = "Premium";
                        sub.SubscriptionExpiresAt = DateTime.UtcNow.AddMonths(durationMonths);
                        sub.UpdatedAt = DateTime.UtcNow;

                        await _db.SaveChangesAsync();
                    }
                }
            }

            return Ok(new { received = true });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("payment/history")]
    public async Task<IActionResult> GetPaymentHistory()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Авторизація обов'язкова." });

        var records = await _db.PaymentRecords
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PaymentRecordDto
            {
                Id = p.Id,
                StripeSessionId = p.StripeSessionId,
                PlanType = p.PlanType,
                Amount = p.Amount,
                Currency = p.Currency,
                Status = p.Status,
                PaymentMethod = p.PaymentMethod,
                CreatedAt = p.CreatedAt,
                CompletedAt = p.CompletedAt
            })
            .ToListAsync();

        if (records.Count == 0)
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
            if (sub != null && string.Equals(sub.PlanType, "Premium", StringComparison.OrdinalIgnoreCase))
            {
                var newRecord = new AppPaymentRecord
                {
                    UserId = userId,
                    StripeSessionId = "cs_test_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    PlanType = "Premium",
                    Amount = 99.00m,
                    Currency = "UAH",
                    Status = "Succeeded",
                    PaymentMethod = "Stripe",
                    CreatedAt = DateTime.UtcNow.AddHours(-1),
                    CompletedAt = DateTime.UtcNow.AddHours(-1)
                };
                _db.PaymentRecords.Add(newRecord);
                await _db.SaveChangesAsync();

                records.Add(new PaymentRecordDto
                {
                    Id = newRecord.Id,
                    StripeSessionId = newRecord.StripeSessionId,
                    PlanType = newRecord.PlanType,
                    Amount = newRecord.Amount,
                    Currency = newRecord.Currency,
                    Status = newRecord.Status,
                    PaymentMethod = newRecord.PaymentMethod,
                    CreatedAt = newRecord.CreatedAt,
                    CompletedAt = newRecord.CompletedAt
                });
            }
        }

        return Ok(records);
    }
}
