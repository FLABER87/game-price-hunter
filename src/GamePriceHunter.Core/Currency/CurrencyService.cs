using System.Text.Json;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Currency;

/// <summary>
/// Keeps exchange rates fresh: serves the disk cache instantly at startup and refreshes it
/// from the first source that answers.
/// </summary>
public sealed class CurrencyService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly IAppLogger _logger;
    private readonly IReadOnlyList<IExchangeRateSource> _sources;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile ExchangeRates? _current;

    public CurrencyService(HttpClient http, string cachePath, IAppLogger logger, IReadOnlyList<IExchangeRateSource>? sources = null)
    {
        _http = http;
        _cachePath = cachePath;
        _logger = logger;
        _sources = sources ?? [new OpenErApiSource(), new FrankfurterSource(), new CbrSource(), new NbuSource()];
    }

    public event EventHandler? RatesChanged;

    public ExchangeRates? Current => _current;

    public bool IsStale => _current is null || DateTimeOffset.UtcNow - _current.Timestamp > MaxAge + TimeSpan.FromHours(12);

    /// <summary>Loads cached rates and refreshes them when older than 12 hours.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadCache();
        var fetchedAt = File.Exists(_cachePath) ? File.GetLastWriteTimeUtc(_cachePath) : DateTime.MinValue;
        if (_current is null || DateTime.UtcNow - fetchedAt > MaxAge)
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var source in _sources)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var rates = await source.FetchAsync(_http, timeout.Token).ConfigureAwait(false);
                    if (rates.Rates.Count < 5)
                        throw new InvalidDataException("Too few rates.");

                    _current = rates;
                    SaveCache(rates);
                    _logger.Info($"Exchange rates updated from {source.Name} ({rates.Rates.Count} currencies).");
                    RatesChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.Warn($"Exchange rate source {source.Name} failed: {ex.Message}");
                }
            }
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Converts money into <paramref name="currency"/>; null when rates are missing.</summary>
    public Money? Convert(Money money, string currency)
    {
        if (string.Equals(money.Currency, currency, StringComparison.OrdinalIgnoreCase))
            return money;
        var amount = _current?.Convert(money.Amount, money.Currency, currency);
        return amount is null ? null : new Money(amount.Value, currency.ToUpperInvariant());
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;
            var rates = JsonSerializer.Deserialize<ExchangeRates>(File.ReadAllText(_cachePath), JsonOptions);
            if (rates is { Rates.Count: > 0 })
                _current = rates;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not read the exchange rate cache: {ex.Message}");
        }
    }

    private void SaveCache(ExchangeRates rates)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(rates, JsonOptions));
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not write the exchange rate cache: {ex.Message}");
        }
    }
}
