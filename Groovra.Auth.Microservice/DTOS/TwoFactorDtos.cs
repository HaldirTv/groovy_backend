namespace Groovra.Auth.Microservice.DTOS;

public class TwoFactorSetupResponseDto
{
    public string SecretKey { get; set; } = string.Empty;
    public string OtpAuthUri { get; set; } = string.Empty;
}

public class TwoFactorVerifyCodeDto
{
    public string Code { get; set; } = string.Empty;
}

public class TwoFactorDisableDto
{
    public string Password { get; set; } = string.Empty;
    public string? Code { get; set; }
}

public class TwoFactorEnableResponseDto
{
    public bool Success { get; set; }
    public List<string> RecoveryCodes { get; set; } = new();
}

public class TwoFactorStatusResponseDto
{
    public bool IsEnabled { get; set; }
}

public class TwoFactorLoginVerifyDto
{
    public string Ticket { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
}
