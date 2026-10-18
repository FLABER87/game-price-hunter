using System.Net;
using System.Runtime.InteropServices;
using GamePriceHunter.Core.Updates;

namespace GamePriceHunter.Core.Tests;

public class UpdateCheckerTests
{
    private const string Repo = "https://github.com/FLABER87/game-price-hunter";

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData(" V0.10.0 ", "0.10.0.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0.0")]
    [InlineData("0.1.0+abc123", "0.1.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void TryParseVersion_accepts_tags_and_version_strings(string text, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("latest")]
    [InlineData("v1.x")]
    public void TryParseVersion_rejects_non_versions(string? text)
    {
        Assert.False(UpdateChecker.TryParseVersion(text, out _));
    }

    [Theory]
    [InlineData("https://github.com/FLABER87/game-price-hunter/releases/tag/v0.2.0", "v0.2.0")]
    [InlineData("https://github.com/FLABER87/game-price-hunter/releases/tag/v0.2.0/", "v0.2.0")]
    [InlineData("https://github.com/flaber87/Game-Price-Hunter/releases/tag/v1.0.0-rc%2B1", "v1.0.0-rc+1")]
    [InlineData("https://github.com/FLABER87/game-price-hunter/releases", null)]
    [InlineData("https://github.com/FLABER87/game-price-hunter/releases/tag/", null)]
    [InlineData("https://github.com/login?return_to=%2Freleases%2Ftag%2Fv9.0.0", null)]
    public void ParseTag_reads_the_tag_from_the_redirect(string location, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.ParseTag(new Uri(location)));
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("v0.10.0", "0.9.5", true)]
    [InlineData("v1.0", "0.99.99", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.1.0.0", false)]
    [InlineData("v0.1.0", "0.2.0", false)]
    [InlineData("v0.2.0", "not-a-version", false)]
    public void IsNewer_compares_numerically(string tag, string current, bool expected)
    {
        var release = new UpdateChecker(new HttpClient(), Repo).CreateRelease(tag)!;
        Assert.Equal(expected, UpdateChecker.IsNewer(release, current));
    }

    [Fact]
    public void CreateRelease_builds_page_and_installer_links()
    {
        var release = new UpdateChecker(new HttpClient(), Repo + "/").CreateRelease("v0.2.0")!;

        Assert.Equal(new Version(0, 2, 0, 0), release.Version);
        Assert.Equal(Repo + "/releases/tag/v0.2.0", release.PageUrl.AbsoluteUri);
        Assert.Equal(
            $"{Repo}/releases/download/v0.2.0/{UpdateChecker.InstallerFileName(RuntimeInformation.ProcessArchitecture)}",
            release.InstallerUrl.AbsoluteUri);
        Assert.Null(new UpdateChecker(new HttpClient(), Repo).CreateRelease("nightly"));
    }

    [Fact]
    public void InstallerFileName_matches_the_setup_script()
    {
        Assert.Equal("GamePriceHunter-Setup.exe", UpdateChecker.InstallerFileName(Architecture.X64));
        Assert.Equal("GamePriceHunter-Setup-arm64.exe", UpdateChecker.InstallerFileName(Architecture.Arm64));
    }

    [Fact]
    public void IsCheckDue_limits_checks_to_once_a_day()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        Assert.True(UpdateChecker.IsCheckDue(null, now));
        Assert.False(UpdateChecker.IsCheckDue(now.AddHours(-23), now));
        Assert.True(UpdateChecker.IsCheckDue(now.AddHours(-24), now));
        Assert.True(UpdateChecker.IsCheckDue(now.AddDays(3), now), "A check time in the future means the clock changed.");
    }

    [Fact]
    public async Task GetLatestReleaseAsync_reads_the_redirect_without_following_it()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal(Repo + "/releases/latest", request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(Repo + "/releases/tag/v0.3.1");
            return response;
        });

        var release = await new UpdateChecker(new HttpClient(handler), Repo).GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal("v0.3.1", release.Tag);
        Assert.Equal(new Version(0, 3, 1, 0), release.Version);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_resolves_relative_redirects()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("/FLABER87/game-price-hunter/releases/tag/v1.4.0", UriKind.Relative);
            return response;
        });

        var release = await new UpdateChecker(new HttpClient(handler), Repo).GetLatestReleaseAsync();

        Assert.Equal("v1.4.0", release?.Tag);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.Found, Repo + "/releases")]
    public async Task GetLatestReleaseAsync_returns_null_without_a_release(HttpStatusCode status, string? location)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (location is not null)
                response.Headers.Location = new Uri(location);
            return response;
        });

        Assert.Null(await new UpdateChecker(new HttpClient(handler), Repo).GetLatestReleaseAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetLatestReleaseAsync_throws_on_unexpected_answers(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateChecker(new HttpClient(handler), Repo).GetLatestReleaseAsync());
    }

    [Fact]
    public void IsInstalledCopy_looks_for_the_uninstaller()
    {
        var directory = Directory.CreateTempSubdirectory("gph-install-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "GamePriceHunter.exe"), "");
            Assert.False(UpdateChecker.IsInstalledCopy(directory.FullName));

            File.WriteAllText(Path.Combine(directory.FullName, "unins000.exe"), "");
            Assert.True(UpdateChecker.IsInstalledCopy(directory.FullName));
            Assert.False(UpdateChecker.IsInstalledCopy(Path.Combine(directory.FullName, "missing")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

public class InstallerDownloaderTests
{
    private static readonly Uri InstallerUrl = new("https://github.com/FLABER87/game-price-hunter/releases/download/v0.2.0/GamePriceHunter-Setup.exe");

    [Fact]
    public async Task DownloadAsync_saves_the_installer_and_reports_progress()
    {
        var payload = new byte[300_000];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var directory = Directory.CreateTempSubdirectory("gph-update-");
        var reports = new List<double>();
        try
        {
            var target = Path.Combine(directory.FullName, "sub", "Setup.exe");
            var path = await InstallerDownloader.DownloadAsync(new HttpClient(handler), InstallerUrl, target, new SyncProgress(reports.Add));

            Assert.Equal(target, path);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(target + ".part"));
            Assert.NotEmpty(reports);
            Assert.Equal(1.0, reports[^1]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_rejects_files_that_are_not_executables()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html>" + new string(' ', 4000) + "</html>"),
        });
        var directory = Directory.CreateTempSubdirectory("gph-update-");
        try
        {
            var target = Path.Combine(directory.FullName, "Setup.exe");
            await Assert.ThrowsAsync<InvalidDataException>(() => InstallerDownloader.DownloadAsync(new HttpClient(handler), InstallerUrl, target));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_fails_on_missing_asset()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var directory = Directory.CreateTempSubdirectory("gph-update-");
        try
        {
            var target = Path.Combine(directory.FullName, "Setup.exe");
            await Assert.ThrowsAsync<HttpRequestException>(() => InstallerDownloader.DownloadAsync(new HttpClient(handler), InstallerUrl, target));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Reports synchronously; <see cref="Progress{T}"/> would post to the thread pool.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
