using System.Net;

namespace GamePriceHunter.Core.Net;

public static class HttpClientFactory
{
    /// <summary>
    /// Many stores serve different (or no) content to unknown clients, so requests identify
    /// as a regular desktop browser.
    /// </summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    /// <summary>
    /// Creates the client shared by all providers. Timeouts are enforced per search through
    /// cancellation tokens, so the client itself never times out.
    /// </summary>
    public static HttpClient Create(CookieContainer? cookies = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseCookies = true,
            CookieContainer = cookies ?? new CookieContainer(),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            MaxConnectionsPerServer = 8,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }
}
