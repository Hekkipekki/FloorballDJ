namespace FloorballDJ.Models;

public enum LicenseAccessKind
{
    None,
    Trial,
    Licensed,
    Expired,
    InternetRequired,
    Invalid
}

public sealed record LicenseEvaluation(
    LicenseAccessKind Kind,
    bool IsAllowed,
    string Message,
    DateTimeOffset? ExpiresAt = null)
{
    public static LicenseEvaluation InternetRequired(string message) =>
        new(LicenseAccessKind.InternetRequired, false, message);
}

public sealed record LicenseDeactivationResult(bool Success, string Message);

internal sealed class LicenseCache
{
    public string Token { get; set; } = "";
    public string? ActivationId { get; set; }
    public string? ActivationSecret { get; set; }
    public DateTimeOffset LastObservedUtc { get; set; }
}

internal sealed class InstallationIdentity
{
    public string InstallationId { get; set; } = "";
}

internal sealed record VerifiedLicenseToken(
    string Kind,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string? Plan,
    string DeviceBinding);

internal sealed class TrialApiResponse
{
    public string Status { get; set; } = "";
    public string? Token { get; set; }
    public DateTimeOffset? TrialStartedAt { get; set; }
    public DateTimeOffset? TrialExpiresAt { get; set; }
}

internal sealed class ActivationApiResponse
{
    public string Status { get; set; } = "";
    public string? ActivationId { get; set; }
    public string? ActivationSecret { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? NextOnlineCheckAt { get; set; }
}

internal sealed class LicenseApiError
{
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? RequestId { get; set; }
}
