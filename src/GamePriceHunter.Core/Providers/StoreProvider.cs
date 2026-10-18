using System.Net;
using System.Text;
using System.Text.Json;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Providers;

/// <summary>Base class with HTTP helpers shared by all store providers.</summary>
public abstract class StoreProvider : IStoreProvider
{
    // Markers that only appear on interstitial challenge pages, not on regular pages
    // served through the same CDN.
    private static readonly string[] ChallengeMarkers =
    [
        "<title>Just a moment...</title>",
        "window._cf_chl_opt",
        "Attention Required! | Cloudflare",
        "_Incapsula_Resource",
        "<title>DDoS-Guard</title>",
        "<TITLE>Access Denied</TITLE>",
        "captcha-delivery.com",
    ];

    static StoreProvider()
    {
        // Some regional stores still serve windows-1251 pages.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    protected StoreProvider(HttpClient http)
    {
        Http = http;
    }

    public abstract ProviderInfo Info { get; }

    protected HttpClient Http { get; }

    public abstract Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken);

    protected static string Encode(string value) => Uri.EscapeDataString(value);

    protected Task<string> GetStringAsync(string url, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        return SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            configure?.Invoke(request);
            return request;
        }, cancellationToken);
    }

    protected async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        var text = await GetStringAsync(url, cancellationToken, request =>
        {
            request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
            configure?.Invoke(request);
        }).ConfigureAwait(false);
        return ParseJson(text);
    }

    protected async Task<JsonDocument> PostJsonAsync(string url, string jsonBody, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        var text = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
            configure?.Invoke(request);
            return request;
        }, cancellationToken).ConfigureAwait(false);
        return ParseJson(text);
    }

    protected Task<string> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        var fields = form.ToList();
        return SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
            configure?.Invoke(request);
            return request;
        }, cancellationToken);
    }

    /// <summary>
    /// Sends a request, retrying once on transient failures, and turns bot-protection
    /// responses into <see cref="ProviderBlockedException"/>.
    /// </summary>
    protected async Task<string> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            try
            {
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (IsBlocked(response.StatusCode, body))
                    throw new ProviderBlockedException($"{Info.Name} refused the request (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);

                if (IsTransient(response.StatusCode) && attempt == 0)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"{Info.Name} returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);

                return body;
            }
            catch (HttpRequestException ex) when (attempt == 0 && ex.StatusCode is null && !cancellationToken.IsCancellationRequested)
            {
                // Connection-level failure (reset, DNS hiccup): one quick retry.
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    protected Offer CreateOffer(string title, string url, Money price) => new()
    {
        ProviderId = Info.Id,
        StoreName = Info.Name,
        StoreKind = Info.Kind,
        StoreUrl = Info.HomeUrl,
        Title = title,
        Url = url,
        Price = price,
    };

    protected static string Absolute(string baseUrl, string url)
    {
        if (string.IsNullOrEmpty(url))
            return baseUrl;
        if (url.StartsWith("//", StringComparison.Ordinal))
            return "https:" + url;
        return Uri.TryCreate(new Uri(baseUrl), url, out var absolute) ? absolute.ToString() : url;
    }

    private static JsonDocument ParseJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            var preview = text.Length > 120 ? text[..120] : text;
            throw new InvalidDataException($"Unexpected response (not JSON): {preview}", ex);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError;

    private static bool IsBlocked(HttpStatusCode status, string body)
    {
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            return true;

        // Challenge pages come with 200 or 503; a plain 503 is treated as transient.
        var head = body.Length > 8192 ? body[..8192] : body;
        foreach (var marker in ChallengeMarkers)
        {
            if (head.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
