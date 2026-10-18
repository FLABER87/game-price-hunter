using System.Net;
using System.Runtime.InteropServices;

namespace GamePriceHunter.Core.Updates;

/// <summary>A published release of the app, identified by its git tag ("v1.2.3").</summary>
public sealed record ReleaseInfo(string Tag, Version Version, Uri PageUrl, Uri InstallerUrl);

/// <summary>
/// Finds the latest release through the public releases page rather than the GitHub API, which
/// rate-limits anonymous clients per IP address: "/releases/latest" redirects to
/// "/releases/tag/&lt;tag&gt;", so the Location header of that redirect names the newest version.
/// </summary>
/// <param name="http">A client that does not follow redirects; see <see cref="CreateHttpClient"/>.</param>
public sealed class UpdateChecker(HttpClient http, string repositoryUrl = UpdateChecker.RepositoryUrl)
{
    public const string RepositoryUrl = "https://github.com/FLABER87/game-price-hunter";

    /// <summary>Startup checks run at most this often.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private const string TagPathMarker = "/releases/tag/";

    private readonly string _repositoryUrl = repositoryUrl.TrimEnd('/');

    public string ReleasesPageUrl => _repositoryUrl + "/releases";

    /// <summary>Creates the client <see cref="UpdateChecker"/> needs: it must see the redirect, not follow it.</summary>
    public static HttpClient CreateHttpClient(string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>
    /// Returns the latest published release, or null when the repository has none. Throws
    /// <see cref="HttpRequestException"/> when GitHub can't be reached or answers unexpectedly.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, _repositoryUrl + "/releases/latest");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            var location = response.Headers.Location;
            if (location is { IsAbsoluteUri: false })
                location = new Uri(request.RequestUri!, location);
            return CreateRelease(ParseTag(location));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        throw new HttpRequestException($"Releases page answered {status} {response.ReasonPhrase}.", null, response.StatusCode);
    }

    /// <summary>Builds the release links for a tag; null when the tag is not a version number.</summary>
    public ReleaseInfo? CreateRelease(string? tag)
    {
        if (tag is null || !TryParseVersion(tag, out var version))
            return null;

        var escaped = Uri.EscapeDataString(tag);
        return new ReleaseInfo(
            tag,
            version,
            new Uri($"{_repositoryUrl}/releases/tag/{escaped}"),
            new Uri($"{_repositoryUrl}/releases/download/{escaped}/{InstallerFileName(RuntimeInformation.ProcessArchitecture)}"));
    }

    /// <summary>Name of the installer attached to every release; must match build\installer\GamePriceHunter.iss.</summary>
    public static string InstallerFileName(Architecture architecture) => architecture == Architecture.X64
        ? "GamePriceHunter-Setup.exe"
        : $"GamePriceHunter-Setup-{architecture.ToString().ToLowerInvariant()}.exe";

    /// <summary>True when <paramref name="release"/> is newer than the running version.</summary>
    public static bool IsNewer(ReleaseInfo release, string currentVersion) =>
        TryParseVersion(currentVersion, out var current) && release.Version > current;

    public static bool IsCheckDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || now - last >= CheckInterval || last > now;

    /// <summary>
    /// True when <paramref name="directory"/> holds a copy installed by the setup (its uninstaller
    /// sits next to the exe), which the setup of a newer version can upgrade in place.
    /// </summary>
    public static bool IsInstalledCopy(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "unins*.exe").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Parses "v1.2.3", "1.2" or "1.2.3-beta" into a version with all four parts set (missing ones are 0).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] is 'v' or 'V')
            span = span[1..];
        var suffix = span.IndexOfAny('-', '+');
        if (suffix >= 0)
            span = span[..suffix];

        if (!Version.TryParse(span, out var parsed))
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    /// <summary>Extracts the tag from ".../releases/tag/v1.2.3"; null for any other address.</summary>
    internal static string? ParseTag(Uri? location)
    {
        if (location is not { IsAbsoluteUri: true })
            return null;

        var path = location.AbsolutePath.TrimEnd('/');
        var index = path.LastIndexOf(TagPathMarker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var tag = Uri.UnescapeDataString(path[(index + TagPathMarker.Length)..]);
        return tag.Length == 0 || tag.Contains('/') ? null : tag;
    }
}
