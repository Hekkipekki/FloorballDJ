using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloorballDJ.Services;

public sealed record UpdateManifest(
    string Version,
    string Channel,
    string InstallerUrl,
    string DownloadPage,
    string ReleaseNotesUrl,
    string Sha256,
    DateTimeOffset? PublishedAt,
    string? Summary);

public sealed class UpdateService
{
    public const string ManifestUrl = "https://floorballdj.netlify.app/update.json";
    private const long MaxInstallerBytes = 800L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+', 2)[0] ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public async Task<UpdateManifest> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Uppdateringsinformationen var tom.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
        {
            return await CheckGitHubFallbackAsync(cancellationToken);
        }
    }

    public bool IsNewer(UpdateManifest manifest) => SemanticVersion.Compare(manifest.Version, CurrentVersion) > 0;

    public async Task<string> DownloadInstallerAsync(UpdateManifest manifest, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloorballDJ", "Updates");
        Directory.CreateDirectory(updateDirectory);
        var safeVersion = Regex.Replace(manifest.Version, "[^0-9A-Za-z.-]", "_");
        var finalPath = Path.Combine(updateDirectory, $"FloorballDJ-Setup-{safeVersion}.exe");
        var temporaryPath = finalPath + ".download";

        try
        {
            using var response = await Client.GetAsync(manifest.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total is > MaxInstallerBytes)
                throw new InvalidDataException("Installationsfilen är större än den tillåtna säkerhetsgränsen.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 128];
                long downloaded = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;
                    if (downloaded > MaxInstallerBytes)
                        throw new InvalidDataException("Installationsfilen är större än den tillåtna säkerhetsgränsen.");
                    if (total is > 0) progress?.Report(downloaded * 100d / total.Value);
                }
            }

            string actualHash;
            await using (var hashStream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(manifest.Sha256)))
                throw new InvalidDataException("Den hämtade installationsfilen klarade inte säkerhetskontrollen (SHA-256). Filen har raderats.");

            File.Move(temporaryPath, finalPath, true);
            progress?.Report(100);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    public static void OpenWebPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || !SemanticVersion.TryParse(manifest.Version, out _))
            throw new InvalidDataException("Uppdateringens versionsnummer är ogiltigt.");
        if (!IsAllowedInstallerUrl(manifest.InstallerUrl) ||
            !IsAllowedWebsiteUrl(manifest.DownloadPage) ||
            !IsAllowedWebsiteUrl(manifest.ReleaseNotesUrl))
            throw new InvalidDataException("Uppdateringsinformationen innehåller en osäker adress.");
        if (!Regex.IsMatch(manifest.Sha256 ?? "", "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("Uppdateringen saknar en giltig SHA-256-kontrollsumma.");
    }

    private static bool IsAllowedInstallerUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/Hekkipekki/FloorballDJ/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedWebsiteUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "floorballdj.netlify.app", StringComparison.OrdinalIgnoreCase);

    private static async Task<UpdateManifest> CheckGitHubFallbackAsync(CancellationToken cancellationToken)
    {
        const string releasesUrl = "https://api.github.com/repos/Hekkipekki/FloorballDJ/releases?per_page=20";
        using var response = await Client.GetAsync(releasesUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var assets = release.GetProperty("assets").EnumerateArray().ToList();
            var installer = assets.FirstOrDefault(asset =>
                string.Equals(asset.GetProperty("name").GetString(), "FloorballDJ-Setup.exe", StringComparison.OrdinalIgnoreCase));
            var checksum = assets.FirstOrDefault(asset =>
                string.Equals(asset.GetProperty("name").GetString(), "FloorballDJ-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase));
            if (installer.ValueKind == JsonValueKind.Undefined || checksum.ValueKind == JsonValueKind.Undefined) continue;

            var checksumUrl = checksum.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsAllowedInstallerUrl(checksumUrl)) continue;
            var checksumText = await Client.GetStringAsync(checksumUrl, cancellationToken);
            var hashMatch = Regex.Match(checksumText, "(?i)\\b[0-9a-f]{64}\\b");
            if (!hashMatch.Success) continue;
            var version = (release.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            var summary = release.TryGetProperty("body", out var body) ? body.GetString() : null;
            if (!string.IsNullOrWhiteSpace(summary) && summary.Length > 600) summary = summary[..600] + "…";
            DateTimeOffset? publishedAt = null;
            if (release.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var parsed))
                publishedAt = parsed;
            var manifest = new UpdateManifest(version, "beta",
                installer.GetProperty("browser_download_url").GetString() ?? "",
                "https://floorballdj.netlify.app/licens/#download",
                "https://floorballdj.netlify.app/changelog/",
                hashMatch.Value.ToLowerInvariant(), publishedAt, summary);
            ValidateManifest(manifest);
            return manifest;
        }
        throw new InvalidDataException("Ingen komplett FloorballDJ-release med installerare och kontrollsumma hittades.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FloorballDJ-Updater/1.0");
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return client;
    }

    private sealed record SemanticVersion(int Major, int Minor, int Patch, string[] PreRelease)
    {
        private static readonly Regex Pattern = new(
            "^v?(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<patch>\\d+)(?:-(?<pre>[0-9A-Za-z.-]+))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool TryParse(string value, out SemanticVersion? result)
        {
            var match = Pattern.Match(value.Trim());
            if (!match.Success)
            {
                result = null;
                return false;
            }
            result = new SemanticVersion(int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value),
                int.Parse(match.Groups["patch"].Value), match.Groups["pre"].Success
                    ? match.Groups["pre"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)
                    : []);
            return true;
        }

        public static int Compare(string left, string right)
        {
            if (!TryParse(left, out var a) || !TryParse(right, out var b) || a is null || b is null) return 0;
            var core = a.Major.CompareTo(b.Major);
            if (core == 0) core = a.Minor.CompareTo(b.Minor);
            if (core == 0) core = a.Patch.CompareTo(b.Patch);
            if (core != 0) return core;
            if (a.PreRelease.Length == 0 || b.PreRelease.Length == 0)
                return a.PreRelease.Length == b.PreRelease.Length ? 0 : a.PreRelease.Length == 0 ? 1 : -1;
            for (var index = 0; index < Math.Max(a.PreRelease.Length, b.PreRelease.Length); index++)
            {
                if (index >= a.PreRelease.Length) return -1;
                if (index >= b.PreRelease.Length) return 1;
                var aNumeric = int.TryParse(a.PreRelease[index], out var aNumber);
                var bNumeric = int.TryParse(b.PreRelease[index], out var bNumber);
                var comparison = aNumeric && bNumeric ? aNumber.CompareTo(bNumber)
                    : aNumeric != bNumeric ? (aNumeric ? -1 : 1)
                    : string.Compare(a.PreRelease[index], b.PreRelease[index], StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
}
