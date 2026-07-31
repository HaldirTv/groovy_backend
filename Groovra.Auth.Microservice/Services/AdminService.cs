using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.DTOS;
using Groovra.Auth.Microservice.Models;
using Groovra.Shared.Constants;
using Groovra.Shared.ServiceResult;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;

namespace Groovra.Auth.Microservice.Services;

public class AdminService
{
    private readonly AuthDbContext _db;
    private readonly ReglogService _reglogService;
    private readonly IHttpClientFactory _httpClientFactory;

    public AdminService(AuthDbContext db, ReglogService reglogService, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _reglogService = reglogService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<PagedResultDto<AdminUserListItemDto>> GetUsersAsync(
        string? search, string? role, int pageNumber, int pageSize, CancellationToken ctoken)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var query = _db.Users
            .Include(u => u.Roles)
            .Include(u => u.Profile)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u => u.Username.ToLower().Contains(s) || u.Email.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(u => u.Roles.Any(r => r.Name == role));

        var totalCount = await query.CountAsync(ctoken);

        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminUserListItemDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                DisplayName = u.Profile != null ? u.Profile.DisplayName : u.Username,
                AvatarUrl = u.Profile != null ? u.Profile.AvatarUrl : string.Empty,
                Roles = u.Roles.Select(r => r.Name).ToList(),
                CreatedAt = u.CreatedAt,
                IsSuspended = u.IsSuspended
            })
            .ToListAsync(ctoken);

        return new PagedResultDto<AdminUserListItemDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<List<AdminArtistApplicationDto>> GetArtistApplicationsAsync(string status, CancellationToken ctoken)
    {
        return await _db.Profiles
            .Include(p => p.User)
            .AsNoTracking()
            .Where(p => p.ArtistApplicationStatus == status)
            .OrderBy(p => p.ArtistApplicationSubmittedAt)
            .Select(p => new AdminArtistApplicationDto
            {
                UserId = p.UserId,
                Username = p.User.Username,
                Email = p.User.Email,
                AvatarUrl = p.AvatarUrl,
                ArtistName = p.ArtistApplicationName,
                Genre = p.ArtistApplicationGenre,
                Country = p.ArtistApplicationCountry,
                Platform = p.ArtistApplicationPlatform,
                Status = p.ArtistApplicationStatus,
                SubmittedAt = p.ArtistApplicationSubmittedAt
            })
            .ToListAsync(ctoken);
    }

    public async Task<ServiceResult<bool>> ApproveArtistApplicationAsync(Guid userId, CancellationToken ctoken)
    {
        var user = await _db.Users
            .Include(u => u.Roles)
            .Include(u => u.ArtistProfile)
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ctoken);

        if (user == null) return ServiceResult<bool>.Fail("Користувача не знайдено.");

        if (!user.Roles.Any(r => r.Name == AppRoles.Artist))
        {
            var artistRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Artist, ctoken);
            if (artistRole == null) return ServiceResult<bool>.Fail("Роль Artist не налаштована.");
            user.Roles.Add(artistRole);
        }

        if (user.ArtistProfile == null)
        {
            var artist = new Artist
            {
                UserId = user.Id,
                Bio = "This is the artist's bio.",
                AvatarUrl = "https://as2.ftcdn.net/v2/jpg/00/64/67/63/1000_F_64676383_LdbmhiNM6Ypzb3FM4PPuFP9rHe7ri8Ju.jpg",
                BannerUrl = string.Empty
            };
            _db.Artists.Add(artist);
            user.ArtistProfile = artist;
        }

        if (user.Profile != null)
        {
            user.Profile.ArtistApplicationStatus = "Approved";
            user.Profile.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ctoken);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> RejectArtistApplicationAsync(Guid userId, CancellationToken ctoken)
    {
        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ctoken);
        if (profile == null) return ServiceResult<bool>.Fail("Профіль не знайдено.");

        if (profile.ArtistApplicationStatus != "Pending")
            return ServiceResult<bool>.Fail("Немає активної заявки для цього користувача.");

        profile.ArtistApplicationStatus = "Rejected";
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ctoken);

        return ServiceResult<bool>.Ok(true);
    }

    public async Task<List<AdminRoleDto>> GetRolesAsync(CancellationToken ctoken)
    {
        var roles = await _db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ctoken);

        var result = new List<AdminRoleDto>(roles.Count);
        foreach (var role in roles)
        {
            var memberCount = await _db.Users
                .AsNoTracking()
                .CountAsync(u => u.Roles.Any(x => x.Id == role.Id), ctoken);

            result.Add(new AdminRoleDto
            {
                Id = role.Id,
                Name = role.Name,
                Description = GetRoleDescription(role.Name),
                MemberCount = memberCount
            });
        }

        return result;
    }

    private static string GetRoleDescription(string roleName) => roleName switch
    {
        AppRoles.Admin => "Повний доступ до всіх функцій системи. Керує користувачами, ролями, налаштуваннями та параметрами безпеки.",
        AppRoles.Artist => "Може публікувати треки й альбоми та керувати власним артист-профілем.",
        AppRoles.Listener => "Стандартний користувач. Має доступ до прослуховування музики, плейлистів та особистого профілю.",
        _ => "Опис ролі не задано."
    };

    public async Task<ServiceResult<bool>> ToggleSuspendUserAsync(Guid userId, CancellationToken ctoken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        if (user == null) return ServiceResult<bool>.Fail("Користувача не знайдено.");

        user.IsSuspended = !user.IsSuspended;
        await _db.SaveChangesAsync(ctoken);

        // Прапорця в БД замало: вже виданий access-token лишається валідним до свого expiry,
        // тому заблокований юзер працював би далі до перезаходу. Відкликаємо всі refresh-сесії -
        // gateway (OnTokenValidated) перевіряє їх наявність у Redis і одразу відхилить запит.
        if (user.IsSuspended)
        {
            await _reglogService.RevokeAllSessionsAsync(user.Email, ctoken);
        }

        return ServiceResult<bool>.Ok(user.IsSuspended);
    }

    public async Task<ServiceResult<bool>> ResetUserPasswordAsync(Guid userId, CancellationToken ctoken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        if (user == null) return ServiceResult<bool>.Fail("Користувача не знайдено.");

        var result = await _reglogService.RequestPasswordResetAsync(user, ctoken);
        if (!result.Success || result.Data == null)
            return ServiceResult<bool>.Fail(result.ErrorMessage ?? "Не вдалося надіслати лист для скидання пароля.");

        await _reglogService.SaveResetCodeAsync(user.Email, result.Data);

        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> ForceLogoutUserAsync(Guid userId, CancellationToken ctoken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        if (user == null) return ServiceResult<bool>.Fail("Користувача не знайдено.");

        await _reglogService.RevokeAllSessionsAsync(user.Email, ctoken);
        return ServiceResult<bool>.Ok(true);
    }

    // ====== SECURITY METHODS ======

    public async Task<SecurityOverviewDto> GetSecurityOverviewAsync(CancellationToken ctoken)
    {
        var since = DateTime.UtcNow.AddHours(-24);

        // Login stats
        var loginsTotal24h = await _db.LoginAudits
            .AsNoTracking()
            .CountAsync(l => l.CreatedAt >= since, ctoken);

        var loginsFailed24h = await _db.LoginAudits
            .AsNoTracking()
            .CountAsync(l => l.CreatedAt >= since && !l.Success, ctoken);

        var failureRate24h = loginsTotal24h > 0 
            ? Math.Round(loginsFailed24h * 100.0 / loginsTotal24h, 1) 
            : 0d;

        // Threat stats
        var recentThreats = await _db.ThreatEvents
            .AsNoTracking()
            .Where(t => t.DetectedAt >= since)
            .Select(t => t.Score)
            .ToListAsync(ctoken);

        var threatsDetected24h = recentThreats.Count;
        var threatAvgScore = recentThreats.Count > 0 ? Math.Round(recentThreats.Average(), 1) : 0d;

        // OAuth risk stats
        var recentOAuthRisks = await _db.OAuthRiskEvents
            .AsNoTracking()
            .Where(o => o.CreatedAt >= since)
            .Select(o => o.RiskScore)
            .ToListAsync(ctoken);

        var oauthRisksDetected24h = recentOAuthRisks.Count;
        var oauthRiskAvgScore = recentOAuthRisks.Count > 0 ? Math.Round(recentOAuthRisks.Average(), 1) : 0d;

        // Critical threats
        var criticalThreats = await _db.ThreatEvents
            .AsNoTracking()
            .CountAsync(t => t.DetectedAt >= since && t.Score >= 80, ctoken);

        return new SecurityOverviewDto
        {
            LoginsTotal24h = loginsTotal24h,
            LoginsFailed24h = loginsFailed24h,
            FailureRate24h = failureRate24h,
            ThreatsDetected24h = threatsDetected24h,
            ThreatAvgScore = threatAvgScore,
            OAuthRisksDetected24h = oauthRisksDetected24h,
            OAuthRiskAvgScore = oauthRiskAvgScore,
            CriticalThreatsCount = criticalThreats
        };
    }

    public async Task<PagedResultDto<LoginAuditDto>> GetLoginAuditsAsync(
        string? search, int pageNumber, int pageSize, CancellationToken ctoken)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var query = _db.LoginAudits.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(l => l.Email.ToLower().Contains(s));
        }

        var totalCount = await query.CountAsync(ctoken);

        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new LoginAuditDto
            {
                Id = l.Id,
                Email = l.Email,
                Success = l.Success,
                IpAddress = l.IpAddress,
                FailureReason = l.FailureReason,
                CreatedAt = l.CreatedAt
            })
            .ToListAsync(ctoken);

        return new PagedResultDto<LoginAuditDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResultDto<ThreatEventDto>> GetThreatsAsync(
        string? type, int pageNumber, int pageSize, CancellationToken ctoken)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var query = _db.ThreatEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(t => t.Type == type);

        var totalCount = await query.CountAsync(ctoken);

        var items = await query
            .OrderByDescending(t => t.DetectedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new ThreatEventDto
            {
                Id = t.Id,
                UserId = t.UserId,
                Type = t.Type,
                Score = t.Score,
                Description = t.Description,
                DetectedAt = t.DetectedAt,
                IsResolved = t.IsResolved
            })
            .ToListAsync(ctoken);

        return new PagedResultDto<ThreatEventDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResultDto<OAuthRiskEventDto>> GetOAuthRisksAsync(
        string? provider, int pageNumber, int pageSize, CancellationToken ctoken)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var query = _db.OAuthRiskEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(provider))
            query = query.Where(o => o.Provider == provider);

        var totalCount = await query.CountAsync(ctoken);

        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OAuthRiskEventDto
            {
                Id = o.Id,
                UserId = o.UserId,
                Provider = o.Provider,
                RiskScore = o.RiskScore,
                Reason = o.Reason,
                CreatedAt = o.CreatedAt
            })
            .ToListAsync(ctoken);

        return new PagedResultDto<OAuthRiskEventDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<SecurityStatsDto> GetSecurityStatsAsync(CancellationToken ctoken)
    {
        var totalLogins = await _db.LoginAudits.AsNoTracking().CountAsync(ctoken);
        var failedLogins = await _db.LoginAudits.AsNoTracking().CountAsync(l => !l.Success, ctoken);
        var totalThreats = await _db.ThreatEvents.AsNoTracking().CountAsync(ctoken);
        var resolvedThreats = await _db.ThreatEvents.AsNoTracking().CountAsync(t => t.IsResolved, ctoken);
        var totalOAuthRisks = await _db.OAuthRiskEvents.AsNoTracking().CountAsync(ctoken);

        var topThreats = await _db.ThreatEvents
            .AsNoTracking()
            .GroupBy(t => t.Type)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToListAsync(ctoken);

        var topProviders = await _db.OAuthRiskEvents
            .AsNoTracking()
            .GroupBy(o => o.Provider)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToListAsync(ctoken);

        return new SecurityStatsDto
        {
            TotalLoginAttempts = totalLogins,
            FailedLoginAttempts = failedLogins,
            TotalThreats = totalThreats,
            ResolvedThreats = resolvedThreats,
            TotalOAuthRisks = totalOAuthRisks,
            TopThreatTypes = topThreats,
            TopOAuthProviders = topProviders
        };
    }

    public async Task<ServiceResult<bool>> ResolveThreatAsync(Guid threatId, CancellationToken ctoken)
    {
        var threat = await _db.ThreatEvents.FirstOrDefaultAsync(t => t.Id == threatId, ctoken);
        if (threat == null)
            return ServiceResult<bool>.Fail("Загрозу не знайдено.");

        threat.IsResolved = true;
        _db.ThreatEvents.Update(threat);
        await _db.SaveChangesAsync(ctoken);

        return ServiceResult<bool>.Ok(true);
    }

    public async Task LogLoginAttemptAsync(Guid? userId, string email, bool success, 
        string? ipAddress, string? failureReason, CancellationToken ctoken)
    {
        var audit = new LoginAudit
        {
            UserId = userId,
            Email = email,
            Success = success,
            IpAddress = ipAddress,
            FailureReason = failureReason,
            CreatedAt = DateTime.UtcNow
        };

        _db.LoginAudits.Add(audit);
        await _db.SaveChangesAsync(ctoken);
    }

    public async Task LogThreatEventAsync(Guid? userId, string type, int score, 
        string description, CancellationToken ctoken)
    {
        var threat = new ThreatEvent
        {
            UserId = userId,
            Type = type,
            Score = score,
            Description = description,
            DetectedAt = DateTime.UtcNow,
            IsResolved = false
        };

        _db.ThreatEvents.Add(threat);
        await _db.SaveChangesAsync(ctoken);
    }

    public async Task LogOAuthRiskEventAsync(Guid? userId, string provider, int riskScore, 
        string reason, CancellationToken ctoken)
    {
        var risk = new OAuthRiskEvent
        {
            UserId = userId,
            Provider = provider,
            RiskScore = riskScore,
            Reason = reason,
            CreatedAt = DateTime.UtcNow
        };

        _db.OAuthRiskEvents.Add(risk);
        await _db.SaveChangesAsync(ctoken);
    }

    public async Task<List<string>> GetAvailableThreatTypesAsync(CancellationToken ctoken)
    {
        return await _db.ThreatEvents
            .AsNoTracking()
            .Select(t => t.Type)
            .Distinct()
            .ToListAsync(ctoken);
    }

    public async Task<List<string>> GetAvailableOAuthProvidersAsync(CancellationToken ctoken)
    {
        return await _db.OAuthRiskEvents
            .AsNoTracking()
            .Select(o => o.Provider)
            .Distinct()
            .ToListAsync(ctoken);
    }

    public async Task<DashboardStatsDto> GetDashboardStatsAsync(CancellationToken ctoken)
    {
        var now = DateTime.UtcNow;
        var lastMonth = now.AddMonths(-1);
        var last30Days = now.AddDays(-30);

        var totalUsers = await _db.Users.AsNoTracking().CountAsync(ctoken);
        var usersLastMonth = await _db.Users.AsNoTracking().CountAsync(u => u.CreatedAt < lastMonth, ctoken);
        var userChangePercent = usersLastMonth > 0 ? Math.Round((totalUsers - usersLastMonth) * 100.0 / usersLastMonth, 1) : 0;
        var targetUsers = 3200000;
        var progressPercent = targetUsers > 0 ? Math.Round(totalUsers * 100.0 / targetUsers, 1) : 0;

        var activeSubscriptions = await _db.Users.AsNoTracking().CountAsync(u => u.Roles.Any(r => r.Name != "Listener"), ctoken);
        var subscriptionRatio = totalUsers > 0 ? Math.Round(activeSubscriptions * 100.0 / totalUsers, 1) : 0;

        var pendingReportsCount = await _db.Profiles
            .AsNoTracking()
            .CountAsync(p => p.ArtistApplicationStatus == "Pending", ctoken);

        var usersLast30 = await _db.Users.AsNoTracking().CountAsync(u => u.CreatedAt >= last30Days, ctoken);
        var avgPerDay = Math.Round(usersLast30 / 30.0, 1);

        var usersActive30 = await _db.Users.AsNoTracking().CountAsync(u => u.CreatedAt >= last30Days, ctoken);
        var retentionPercent = totalUsers > 0 ? Math.Round(usersActive30 * 100.0 / totalUsers, 1) : 92;

        var months = new List<RevenueMonthDto>
        {
            new() { Label = "СІЧЕНЬ", Value = 70.4, Active = false },
            new() { Label = "ЛЮТИЙ", Value = 114.4, Active = false },
            new() { Label = "БЕРЕЗЕНЬ", Value = 96.8, Active = false },
            new() { Label = "КВІТЕНЬ", Value = 140.8, Active = false },
            new() { Label = "ТРАВЕНЬ", Value = 167.2, Active = false },
            new() { Label = "ЧЕРВЕНЬ", Value = 132, Active = false },
            new() { Label = "ЛИПЕНЬ", Value = 176, Active = true }
        };

        return new DashboardStatsDto
        {
            TotalUsers = new TotalUsersStatsDto
            {
                Value = FormatNumber(totalUsers),
                ChangePercent = userChangePercent,
                ProgressPercent = progressPercent,
                TargetLabel = "Активна ціль: 3,2 млн"
            },
            MonthlyRevenue = new MonthlyRevenueStatsDto
            {
                Value = "$4.2M",
                PeriodLabel = "ПОТОЧНИЙ ТРЕТІЙ КВАРТАЛ",
                Months = months
            },
            ActiveSubscriptions = new ActiveSubscriptionsStatsDto
            {
                Value = FormatNumber(activeSubscriptions),
                RatioLabel = $"співвідношення {subscriptionRatio}%",
                Caption = "Конверсія преміум-класу зросла на 4,2% з моменту останньої синхронізації."
            },
            AiGeneratedTracks = new AiGeneratedTracksStatsDto
            {
                Value = FormatNumber(await GetAITracksCountAsync(ctoken))
            },
            PendingReports = new PendingReportsStatsDto
            {
                Value = pendingReportsCount.ToString(),
                Caption = pendingReportsCount > 0
                    ? "Потребує негайної уваги адміністратора."
                    : "Немає запитів, що очікують на розгляд."
            },
            GrowthRate = new GrowthRateStatsDto
            {
                AvgPerDayLabel = $"+{avgPerDay}k",
                RetentionLabel = $"{retentionPercent}%"
            }
        };
    }

    public async Task<List<ActivityFeedItemDto>> GetActivityFeedAsync(CancellationToken ctoken)
    {
        var items = new List<ActivityFeedItemDto>();

        var pendingApplications = await _db.Profiles
            .AsNoTracking()
            .Where(p => p.ArtistApplicationStatus == "Pending")
            .OrderByDescending(p => p.ArtistApplicationSubmittedAt)
            .Take(2)
            .ToListAsync(ctoken);

        foreach (var app in pendingApplications)
        {
            var timeAgo = GetTimeAgo(app.ArtistApplicationSubmittedAt ?? DateTime.UtcNow);
            items.Add(new ActivityFeedItemDto
            {
                Id = $"artist-{app.UserId}",
                Tone = "neutral",
                IconType = "UserFlag",
                Title = "Запит на перевірку нового виконавця",
                Subtitle = $"Ім'я виконавця: {app.ArtistApplicationName} • Запит надіслано {timeAgo}",
                Badge = "ОЧІКУЄ",
                CreatedAt = app.ArtistApplicationSubmittedAt ?? DateTime.UtcNow
            });
        }

        var recentThreats = await _db.ThreatEvents
            .AsNoTracking()
            .Where(t => !t.IsResolved && t.Score >= 70)
            .OrderByDescending(t => t.DetectedAt)
            .Take(2)
            .ToListAsync(ctoken);

        foreach (var threat in recentThreats)
        {
            items.Add(new ActivityFeedItemDto
            {
                Id = $"threat-{threat.Id}",
                Tone = "warning",
                IconType = "Alert",
                Title = $"Виявлено загрозу безпеки – {threat.Type}",
                Subtitle = $"{threat.Description} • Оцінка загрози: {threat.Score}",
                Badge = "ТЕРМІНОВО",
                CreatedAt = threat.DetectedAt
            });
        }

        var recentUsers = await _db.Users
            .AsNoTracking()
            .Where(u => u.Roles.Count > 1)
            .OrderByDescending(u => u.CreatedAt)
            .Take(1)
            .ToListAsync(ctoken);

        foreach (var user in recentUsers)
        {
            items.Add(new ActivityFeedItemDto
            {
                Id = $"user-{user.Id}",
                Tone = "accent",
                IconType = "Star",
                Title = "Оновлення рівня підписки",
                Subtitle = $"Користувач: {user.Username} • Оновлено до PRO+",
                Badge = "УСПІХ",
                CreatedAt = user.CreatedAt
            });
        }

        items.Add(new ActivityFeedItemDto
        {
            Id = "system-patch",
            Tone = "muted",
            IconType = "Cpu",
            Title = "Розгорнуто системний патч",
            Subtitle = "Стабільний реліз Core Engine версії 2.4.1 • Затримка зменшена на 14 мс",
            Badge = "СИСТЕМА",
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        });

        return items.OrderByDescending(i => i.CreatedAt).Take(4).ToList();
    }

    private static string FormatNumber(int number)
    {
        if (number >= 1000000)
            return $"{Math.Round(number / 1000000.0, 1)}M";
        if (number >= 1000)
            return $"{Math.Round(number / 1000.0, 1)}k";
        return number.ToString();
    }

    private static string GetTimeAgo(DateTime dateTime)
    {
        var span = DateTime.UtcNow - dateTime;
        if (span.TotalMinutes < 1) return "щойно";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} хв. тому";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} год. тому";
        return $"{(int)span.TotalDays} дн. тому";
    }

    private async Task<int> GetAITracksCountAsync(CancellationToken ctoken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient("MusicService");
            var response = await httpClient.GetAsync("/music/stats/ai-tracks-count", ctoken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<AITracksCountResponse>(ctoken);
                return result?.Count ?? 0;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    // ====== CREATE USER ======
    public async Task<ServiceResult<Guid>> CreateUserAsync(string username, string email, string password, string roleName, CancellationToken ctoken)
    {
        // Check if email already exists
        if (await _db.Users.AnyAsync(u => u.Email == email, ctoken))
            return ServiceResult<Guid>.Fail("Користувач з таким email вже існує.");

        // Check if username already exists
        if (await _db.Users.AnyAsync(u => u.Username == username, ctoken))
            return ServiceResult<Guid>.Fail("Користувач з таким ім'ям вже існує.");

        // Find or create role
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ctoken)
            ?? new Role { Name = roleName };

        if (role.Id == Guid.Empty)
            _db.Roles.Add(role);

        // Create user
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Roles = { role },
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ctoken);

        return ServiceResult<Guid>.Ok(user.Id);
    }

    // ====== BULK APPROVE ARTIST APPLICATIONS ======
    public async Task<ServiceResult<int>> BulkApproveArtistApplicationsAsync(List<Guid> userIds, CancellationToken ctoken)
    {
        if (userIds == null || userIds.Count == 0)
            return ServiceResult<int>.Fail("Не передано жодного ID користувача.");

        var artistRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Artist, ctoken);
        if (artistRole == null)
            return ServiceResult<int>.Fail("Роль Artist не налаштована.");

        var approved = 0;
        foreach (var userId in userIds)
        {
            var user = await _db.Users
                .Include(u => u.Roles)
                .Include(u => u.ArtistProfile)
                .Include(u => u.Profile)
                .FirstOrDefaultAsync(u => u.Id == userId, ctoken);

            if (user == null) continue;

            // Add artist role if not present
            if (!user.Roles.Any(r => r.Name == AppRoles.Artist))
                user.Roles.Add(artistRole);

            // Create artist profile if not present
            if (user.ArtistProfile == null)
            {
                var artist = new Artist
                {
                    UserId = user.Id,
                    Bio = "Артист зареєстрований через адмін-панель.",
                    AvatarUrl = "https://via.placeholder.com/150",
                    BannerUrl = string.Empty
                };
                _db.Artists.Add(artist);
                user.ArtistProfile = artist;
            }

            // Update profile application status
            if (user.Profile != null && (user.Profile.ArtistApplicationStatus == "Pending" || user.Profile.ArtistApplicationStatus == "Approved"))
            {
                user.Profile.ArtistApplicationStatus = "Approved";
                user.Profile.UpdatedAt = DateTime.UtcNow;
                approved++;
            }
        }

        await _db.SaveChangesAsync(ctoken);

        if (approved == 0)
            return ServiceResult<int>.Fail("Не знайдено заявок для затвердження.");

        return ServiceResult<int>.Ok(approved);
    }

    // ====== BULK REJECT ARTIST APPLICATIONS ======
    public async Task<ServiceResult<int>> BulkRejectArtistApplicationsAsync(List<Guid> userIds, CancellationToken ctoken)
    {
        if (userIds == null || userIds.Count == 0)
            return ServiceResult<int>.Fail("Не передано жодного ID користувача.");

        var rejected = 0;
        foreach (var userId in userIds)
        {
            var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ctoken);
            if (profile == null) continue;

            if (profile.ArtistApplicationStatus == "Pending")
            {
                profile.ArtistApplicationStatus = "Rejected";
                profile.UpdatedAt = DateTime.UtcNow;
                rejected++;
            }
        }

        await _db.SaveChangesAsync(ctoken);

        if (rejected == 0)
            return ServiceResult<int>.Fail("Не знайдено заявок для відхилення.");

        return ServiceResult<int>.Ok(rejected);
    }

    // ====== CREATE ROLE ======
    public async Task<ServiceResult<Guid>> CreateRoleAsync(string name, string description, CancellationToken ctoken)
    {
        if (await _db.Roles.AnyAsync(r => r.Name == name, ctoken))
            return ServiceResult<Guid>.Fail("Роль з таким ім'ям вже існує.");

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = name
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ctoken);

        return ServiceResult<Guid>.Ok(role.Id);
    }

    // ====== DELETE ROLE ======
    public async Task<ServiceResult<bool>> DeleteRoleAsync(Guid roleId, CancellationToken ctoken)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ctoken);
        if (role == null)
            return ServiceResult<bool>.Fail("Роль не знайдено.");

        // Prevent deleting predefined roles
        if (role.Name == AppRoles.Admin || role.Name == AppRoles.Artist || role.Name == AppRoles.Listener)
            return ServiceResult<bool>.Fail("Не можна видалити вбудовану роль.");

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ctoken);

        return ServiceResult<bool>.Ok(true);
    }

    // ====== ADD USER TO ROLE ======
    public async Task<ServiceResult<bool>> AddUserToRoleAsync(Guid roleId, Guid userId, CancellationToken ctoken)
    {
        var user = await _db.Users
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Id == userId, ctoken);

        if (user == null)
            return ServiceResult<bool>.Fail("Користувача не знайдено.");

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ctoken);
        if (role == null)
            return ServiceResult<bool>.Fail("Роль не знайдено.");

        if (user.Roles.Any(r => r.Id == roleId))
            return ServiceResult<bool>.Fail("Користувач вже має цю роль.");

        user.Roles.Add(role);
        await _db.SaveChangesAsync(ctoken);

        return ServiceResult<bool>.Ok(true);
    }

    // ====== GET ROLE CAPABILITIES ======
    public async Task<RoleCapabilitiesDto> GetRoleCapabilitiesAsync(Guid roleId, CancellationToken ctoken)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ctoken);
        if (role == null)
            return new RoleCapabilitiesDto { Name = "Не знайдено", Permissions = new List<RoleCapabilityDto>() };

        var capabilities = new List<RoleCapabilityDto>
        {
            new RoleCapabilityDto { Feature = "Dashboard (Інформаційна панель)", CanView = true, CanEdit = role.Name == AppRoles.Admin, CanDelete = false },
            new RoleCapabilityDto { Feature = "Content (Контент - треки)", CanView = true, CanEdit = role.Name == AppRoles.Artist || role.Name == AppRoles.Admin, CanDelete = role.Name == AppRoles.Admin },
            new RoleCapabilityDto { Feature = "Users (Користувачі)", CanView = role.Name == AppRoles.Admin, CanEdit = role.Name == AppRoles.Admin, CanDelete = false },
            new RoleCapabilityDto { Feature = "Security (Безпека)", CanView = role.Name == AppRoles.Admin, CanEdit = false, CanDelete = false },
            new RoleCapabilityDto { Feature = "Roles (Ролі та дозволи)", CanView = role.Name == AppRoles.Admin, CanEdit = role.Name == AppRoles.Admin, CanDelete = false },
            new RoleCapabilityDto { Feature = "Playlists (Плейлисти)", CanView = true, CanEdit = role.Name == AppRoles.Artist || role.Name == AppRoles.Admin, CanDelete = role.Name == AppRoles.Admin },
            new RoleCapabilityDto { Feature = "Analytics (Аналітика)", CanView = role.Name == AppRoles.Artist || role.Name == AppRoles.Admin, CanEdit = false, CanDelete = false },
            new RoleCapabilityDto { Feature = "Settings (Налаштування)", CanView = true, CanEdit = role.Name == AppRoles.Admin, CanDelete = false }
        };

        return new RoleCapabilitiesDto
        {
            Name = role.Name,
            Description = GetRoleDescription(role.Name),
            Permissions = capabilities
        };
    }

    // ====== GET USER BY ID ======
    public async Task<AdminUserListItemDto?> GetUserByIdAsync(Guid userId, CancellationToken ctoken)
    {
        var user = await _db.Users
            .Include(u => u.Roles)
            .Include(u => u.Profile)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ctoken);

        if (user == null) return null;

        return new AdminUserListItemDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            DisplayName = user.Profile != null ? user.Profile.DisplayName : user.Username,
            AvatarUrl = user.Profile != null ? user.Profile.AvatarUrl : string.Empty,
            Roles = user.Roles.Select(r => r.Name).ToList(),
            CreatedAt = user.CreatedAt,
            IsSuspended = user.IsSuspended
        };
    }

    private class AITracksCountResponse
    {
        public int Count { get; set; }
    }
}