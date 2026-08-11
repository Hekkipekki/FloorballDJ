using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloorballDJ.Models;
using Microsoft.Win32;

namespace FloorballDJ.Services;

public sealed class LicenseService
{
    private const string ApiBaseUrl = "https://floorballdj-licensing-api.netlify.app";
    private const string ExpectedIssuer = ApiBaseUrl;
    private const string ExpectedAudience = "floorballdj-desktop";
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFPd3QC4LcpCavU8iqWRqHxIv3KPf
        AC+/0DapMgH7ltINYrIqlMpW3ZUYZfCPGF5nXmxoyx+Xytte19y6N5Qi9w==
        -----END PUBLIC KEY-----
        """;
    private static readonly IReadOnlyDictionary<string, string> TrustedSigningKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["floorballdj-licensing-v1"] = PublicKeyPem
        };
    private static readonly byte[] StorageEntropy = Encoding.UTF8.GetBytes("FloorballDJ.Licensing.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _storageDirectory;
    private readonly string _identityPath;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LicenseCache? _cache;

    public LicenseEvaluation Current { get; private set; } =
        new(LicenseAccessKind.None, false, "Licensen har inte kontrollerats ännu.");

    public LicenseService()
    {
        _storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloorballDJ", "Licensing");
        _identityPath = Path.Combine(_storageDirectory, "installation.json");
        _cachePath = Path.Combine(_storageDirectory, "license.dat");
        _http = new HttpClient
        {
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(8)
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"FloorballDJ/{version}");
    }

    public async Task<LicenseEvaluation> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cache ??= LoadCache();
            var now = DateTimeOffset.UtcNow;
            var clockMovedBack = _cache is { LastObservedUtc: var last } && now < last.AddMinutes(-5);

            // Trials are authorized by Supabase server time on every launch. This prevents a
            // frozen or rolled-back Windows clock from extending a seven-day trial.
            if (_cache is { Token.Length: > 0 } trialCache &&
                TryVerifyToken(trialCache.Token, now, out var cachedTrial) && cachedTrial.Kind == "trial")
            {
                var onlineTrial = await TryStartTrialAsync(cancellationToken);
                return SetCurrent(onlineTrial ?? LicenseEvaluation.InternetRequired(
                    "Internet krävs vid start under provperioden för att kontrollera återstående tid."));
            }

            if (!clockMovedBack && _cache is { Token.Length: > 0 } cache &&
                TryVerifyToken(cache.Token, now, out var verified))
            {
                cache.LastObservedUtc = now > cache.LastObservedUtc ? now : cache.LastObservedUtc;
                SaveCache(cache);

                if (verified.Kind == "license" && verified.ExpiresAt <= now.AddDays(7) && HasActivation(cache))
                {
                    var refreshed = await TryRefreshAsync(cache, cancellationToken);
                    if (refreshed is not null) return SetCurrent(refreshed);
                }

                return SetCurrent(ToEvaluation(verified));
            }

            if (_cache is { } expiredCache && HasActivation(expiredCache))
            {
                var refreshed = await TryRefreshAsync(expiredCache, cancellationToken);
                if (refreshed is not null) return SetCurrent(refreshed);
                return SetCurrent(LicenseEvaluation.InternetRequired(
                    "Licensen behöver kontrolleras online innan FloorballDJ kan starta."));
            }

            var trial = await TryStartTrialAsync(cancellationToken);
            if (trial is not null) return SetCurrent(trial);

            return SetCurrent(LicenseEvaluation.InternetRequired(
                "Anslut datorn till internet för att starta provperioden eller aktivera en licens."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicenseEvaluation> ActivateAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return SetCurrent(new LicenseEvaluation(LicenseAccessKind.Invalid, false, "Ange en licensnyckel."));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var response = await PostAsync("/api/v1/license/activate", new
            {
                licenseKey = key.Trim(),
                installationId = GetOrCreateInstallationId(),
                machineFingerprint = GetMachineFingerprint(),
                machineName = Environment.MachineName
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return SetCurrent(new LicenseEvaluation(LicenseAccessKind.Invalid, false,
                    await ReadErrorAsync(response, "Licensnyckeln kunde inte aktiveras.", cancellationToken)));

            var result = await response.Content.ReadFromJsonAsync<ActivationApiResponse>(JsonOptions, cancellationToken);
            if (result?.Token is null || result.ActivationId is null || result.ActivationSecret is null ||
                !TryVerifyToken(result.Token, DateTimeOffset.UtcNow, out var verified) || verified.Kind != "license")
                return SetCurrent(new LicenseEvaluation(LicenseAccessKind.Invalid, false,
                    "Licenstjänsten returnerade ett ogiltigt licensbevis."));

            _cache = new LicenseCache
            {
                Token = result.Token,
                ActivationId = result.ActivationId,
                ActivationSecret = result.ActivationSecret,
                LastObservedUtc = DateTimeOffset.UtcNow
            };
            SaveCache(_cache);
            return SetCurrent(ToEvaluation(verified));
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return SetCurrent(LicenseEvaluation.InternetRequired(
                "Kunde inte nå licenstjänsten. Kontrollera internetanslutningen och försök igen."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicenseDeactivationResult> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cache ??= LoadCache();
            if (_cache is not { ActivationId.Length: > 0, ActivationSecret.Length: > 0 } cache)
            {
                ClearCache();
                Current = new LicenseEvaluation(LicenseAccessKind.None, false, "Det lokala licensbeviset har tagits bort.");
                return new LicenseDeactivationResult(true, Current.Message);
            }

            using var response = await PostAsync("/api/v1/license/deactivate", new
            {
                activationId = cache.ActivationId,
                activationSecret = cache.ActivationSecret,
                installationId = GetOrCreateInstallationId()
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadErrorAsync(response,
                    "Licensen kunde inte avaktiveras på servern.", cancellationToken);
                return new LicenseDeactivationResult(false, message);
            }

            ClearCache();
            Current = new LicenseEvaluation(LicenseAccessKind.None, false, "Licensen har avaktiverats.");
            return new LicenseDeactivationResult(true, Current.Message);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return new LicenseDeactivationResult(false,
                "Kunde inte nå licenstjänsten. Kontrollera internetanslutningen och försök igen.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LicenseEvaluation?> TryStartTrialAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync("/api/v1/trial/start", new
            {
                installationId = GetOrCreateInstallationId(),
                machineFingerprint = GetMachineFingerprint()
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 403)
                    return new LicenseEvaluation(LicenseAccessKind.Expired, false,
                        "Den sju dagar långa provperioden har gått ut. Aktivera en licens för att fortsätta.");
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<TrialApiResponse>(JsonOptions, cancellationToken);
            if (result?.Token is null || !TryVerifyToken(result.Token, DateTimeOffset.UtcNow, out var verified) ||
                verified.Kind != "trial") return null;

            _cache = new LicenseCache { Token = result.Token, LastObservedUtc = DateTimeOffset.UtcNow };
            SaveCache(_cache);
            return ToEvaluation(verified);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return null;
        }
    }

    private async Task<LicenseEvaluation?> TryRefreshAsync(LicenseCache cache, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync("/api/v1/license/refresh", new
            {
                activationId = cache.ActivationId,
                activationSecret = cache.ActivationSecret,
                installationId = GetOrCreateInstallationId(),
                machineFingerprint = GetMachineFingerprint(),
                machineName = Environment.MachineName
            }, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var result = await response.Content.ReadFromJsonAsync<ActivationApiResponse>(JsonOptions, cancellationToken);
            if (result?.Token is null || !TryVerifyToken(result.Token, DateTimeOffset.UtcNow, out var verified) ||
                verified.Kind != "license") return null;

            cache.Token = result.Token;
            cache.LastObservedUtc = DateTimeOffset.UtcNow;
            SaveCache(cache);
            return ToEvaluation(verified);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, CancellationToken cancellationToken) =>
        await _http.PostAsJsonAsync(path, body, JsonOptions, cancellationToken);

    private static bool HasActivation(LicenseCache cache) =>
        !string.IsNullOrWhiteSpace(cache.ActivationId) && !string.IsNullOrWhiteSpace(cache.ActivationSecret);

    private LicenseEvaluation SetCurrent(LicenseEvaluation evaluation)
    {
        Current = evaluation;
        return evaluation;
    }

    private static LicenseEvaluation ToEvaluation(VerifiedLicenseToken token)
    {
        var localExpiry = token.ExpiresAt.ToLocalTime();
        return token.Kind == "trial"
            ? new LicenseEvaluation(LicenseAccessKind.Trial, true,
                $"Provperiod aktiv till {localExpiry:yyyy-MM-dd HH:mm}.", token.ExpiresAt)
            : new LicenseEvaluation(LicenseAccessKind.Licensed, true,
                $"Licensen är aktiv. Nästa onlinekontroll senast {localExpiry:yyyy-MM-dd}.", token.ExpiresAt);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        string fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<LicenseApiError>(JsonOptions, cancellationToken);
            return error?.Error switch
            {
                "license_not_found" => "Licensnyckeln hittades inte.",
                "license_inactive" => "Licensen är inte aktiv.",
                "license_expired" => "Licensen har gått ut.",
                "activation_limit_reached" => "Licensen används redan på maximalt antal datorer.",
                "internal_error" => string.IsNullOrWhiteSpace(error.RequestId)
                    ? "Licenstjänsten fick ett internt fel. Försök igen om en stund."
                    : $"Licenstjänsten fick ett internt fel. Referens: {error.RequestId}",
                _ => error?.Message ?? fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsNetworkError(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or IOException;

    private bool TryVerifyToken(string token, DateTimeOffset now, out VerifiedLicenseToken verified)
    {
        verified = null!;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;
            using var header = JsonDocument.Parse(DecodeBase64Url(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256") return false;
            if (!header.RootElement.TryGetProperty("kid", out var kidValue) ||
                kidValue.GetString() is not { } keyId || !TrustedSigningKeys.TryGetValue(keyId, out var publicKeyPem))
                return false;

            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var signedData = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            if (!key.VerifyData(signedData, DecodeBase64Url(parts[2]), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return false;

            using var payload = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var root = payload.RootElement;
            if (!string.Equals(root.GetProperty("iss").GetString()?.TrimEnd('/'),
                    ExpectedIssuer.TrimEnd('/'), StringComparison.Ordinal) || !HasAudience(root, ExpectedAudience))
                return false;

            var kind = root.GetProperty("kind").GetString();
            if (kind is not ("trial" or "license")) return false;
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("iat").GetInt64());
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());
            if (issuedAt > now.AddMinutes(5) || expiresAt <= now.AddMinutes(-1)) return false;
            var plan = root.TryGetProperty("plan", out var planValue) ? planValue.GetString() : null;
            if (!root.TryGetProperty("device", out var deviceValue) ||
                deviceValue.GetString() is not { Length: 64 } signedDevice ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(signedDevice),
                    Encoding.ASCII.GetBytes(GetDeviceBinding()))) return false;
            verified = new VerifiedLicenseToken(kind, issuedAt, expiresAt, plan, signedDevice);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool HasAudience(JsonElement payload, string expected)
    {
        if (!payload.TryGetProperty("aud", out var audience)) return false;
        return audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == expected,
            JsonValueKind.Array => audience.EnumerateArray().Any(x => x.GetString() == expected),
            _ => false
        };
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(normalized);
    }

    private string GetOrCreateInstallationId()
    {
        Directory.CreateDirectory(_storageDirectory);
        try
        {
            if (File.Exists(_identityPath))
            {
                var identity = JsonSerializer.Deserialize<InstallationIdentity>(File.ReadAllText(_identityPath), JsonOptions);
                if (identity?.InstallationId.Length >= 16) return identity.InstallationId;
            }
        }
        catch { }

        var created = new InstallationIdentity
        {
            InstallationId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        };
        WriteAtomic(_identityPath, JsonSerializer.Serialize(created, JsonOptions));
        return created.InstallationId;
    }

    private static string GetMachineFingerprint()
    {
        var machineGuid = ReadMachineGuid();
        var source = string.IsNullOrWhiteSpace(machineGuid)
            ? $"{Environment.MachineName}|{Environment.SystemDirectory}|{Environment.OSVersion.Version}"
            : machineGuid;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"FloorballDJ|{source}"))).ToLowerInvariant();
    }

    private string GetDeviceBinding()
    {
        var value = $"FloorballDJ.Device.v1|{GetOrCreateInstallationId()}|{GetMachineFingerprint()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string? ReadMachineGuid()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key?.GetValue("MachineGuid") is string value && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            catch { }
        }
        return null;
    }

    private LicenseCache? LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var encrypted = File.ReadAllBytes(_cachePath);
            var json = ProtectedData.Unprotect(encrypted, StorageEntropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<LicenseCache>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void SaveCache(LicenseCache cache)
    {
        Directory.CreateDirectory(_storageDirectory);
        var json = JsonSerializer.SerializeToUtf8Bytes(cache, JsonOptions);
        var encrypted = ProtectedData.Protect(json, StorageEntropy, DataProtectionScope.CurrentUser);
        var temporary = _cachePath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, _cachePath, true);
    }

    private void ClearCache()
    {
        _cache = null;
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, Encoding.UTF8);
        File.Move(temporary, path, true);
    }
}
