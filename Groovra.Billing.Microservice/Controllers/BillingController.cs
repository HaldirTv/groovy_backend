using System;
using System.Threading.Tasks;
using Groovra.Billing.Microservice.Data;
using Groovra.Billing.Microservice.DTOs;
using Groovra.Billing.Microservice.Models;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Billing.Microservice.Controllers;

[ApiController]
[Route("billing")]
public class BillingController : ControllerBase
{
    private readonly BillingDbContext _db;
    private const int FREE_AI_MIX_LIMIT = 3;

    public BillingController(BillingDbContext db)
    {
        _db = db;
    }

    private bool TryGetUserId(out Guid userId) => HttpContext.TryGetUserId(out userId);

    [HttpGet("subscription")]
    public async Task<IActionResult> GetSubscriptionStatus()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Авторизація обов'язкова." });

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (sub == null)
        {
            sub = new UserSubscription { UserId = userId, PlanType = "Free", AiMixUsageCount = 0 };
            _db.Subscriptions.Add(sub);
            await _db.SaveChangesAsync();
        }

        bool isPremium = string.Equals(sub.PlanType, "Premium", StringComparison.OrdinalIgnoreCase) &&
                         (!sub.SubscriptionExpiresAt.HasValue || sub.SubscriptionExpiresAt.Value > DateTime.UtcNow);

        int remaining = isPremium ? 999999 : Math.Max(0, FREE_AI_MIX_LIMIT - sub.AiMixUsageCount);

        return Ok(new SubscriptionStatusDto
        {
            PlanType = isPremium ? "Premium" : "Free",
            AiMixUsageCount = sub.AiMixUsageCount,
            AiMixLimit = isPremium ? -1 : FREE_AI_MIX_LIMIT,
            RemainingAiMixes = remaining,
            IsActivePremium = isPremium,
            SubscriptionExpiresAt = sub.SubscriptionExpiresAt
        });
    }

    [HttpPost("subscribe")]
    public async Task<IActionResult> UpgradeSubscription([FromBody] UpgradeSubscriptionDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Авторизація обов'язкова." });

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (sub == null)
        {
            sub = new UserSubscription { UserId = userId };
            _db.Subscriptions.Add(sub);
        }

        if (string.Equals(dto.PlanType, "Premium", StringComparison.OrdinalIgnoreCase))
        {
            sub.PlanType = "Premium";
            sub.SubscriptionExpiresAt = DateTime.UtcNow.AddMonths(dto.DurationMonths > 0 ? dto.DurationMonths : 1);
        }
        else
        {
            sub.PlanType = "Free";
            sub.SubscriptionExpiresAt = null;
        }

        sub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        bool isPremium = string.Equals(sub.PlanType, "Premium", StringComparison.OrdinalIgnoreCase);
        int remaining = isPremium ? 999999 : Math.Max(0, FREE_AI_MIX_LIMIT - sub.AiMixUsageCount);

        return Ok(new SubscriptionStatusDto
        {
            PlanType = sub.PlanType,
            AiMixUsageCount = sub.AiMixUsageCount,
            AiMixLimit = isPremium ? -1 : FREE_AI_MIX_LIMIT,
            RemainingAiMixes = remaining,
            IsActivePremium = isPremium,
            SubscriptionExpiresAt = sub.SubscriptionExpiresAt
        });
    }

    [HttpPost("cancel-subscription")]
    public async Task<IActionResult> CancelSubscription()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Авторизація обов'язкова." });

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (sub != null)
        {
            sub.PlanType = "Free";
            sub.SubscriptionExpiresAt = null;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok(new SubscriptionStatusDto
        {
            PlanType = "Free",
            AiMixUsageCount = sub?.AiMixUsageCount ?? 0,
            AiMixLimit = FREE_AI_MIX_LIMIT,
            RemainingAiMixes = Math.Max(0, FREE_AI_MIX_LIMIT - (sub?.AiMixUsageCount ?? 0)),
            IsActivePremium = false,
            SubscriptionExpiresAt = null
        });
    }

    [HttpPost("ai-mix/check-and-increment")]
    public async Task<IActionResult> CheckAndIncrementAiMix()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Авторизація обов'язкова." });

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (sub == null)
        {
            sub = new UserSubscription { UserId = userId, PlanType = "Free", AiMixUsageCount = 0 };
            _db.Subscriptions.Add(sub);
            await _db.SaveChangesAsync();
        }

        bool isPremium = string.Equals(sub.PlanType, "Premium", StringComparison.OrdinalIgnoreCase) &&
                         (!sub.SubscriptionExpiresAt.HasValue || sub.SubscriptionExpiresAt.Value > DateTime.UtcNow);

        if (!isPremium && sub.AiMixUsageCount >= FREE_AI_MIX_LIMIT)
        {
            return StatusCode(403, new CheckAiMixResponseDto
            {
                Allowed = false,
                RemainingCount = 0,
                Message = "Ви вичерпали ліміт (3 з 3) безкоштовних генерацій AI-міксу. Оформіть Groovra Premium для безлімітного доступу!"
            });
        }

        if (!isPremium)
        {
            sub.AiMixUsageCount++;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        int remaining = isPremium ? 999999 : Math.Max(0, FREE_AI_MIX_LIMIT - sub.AiMixUsageCount);

        return Ok(new CheckAiMixResponseDto
        {
            Allowed = true,
            RemainingCount = remaining,
            Message = isPremium ? "Безлімітний AI-мікс активний." : $"Залишилося спроб: {remaining} з {FREE_AI_MIX_LIMIT}."
        });
    }
}
