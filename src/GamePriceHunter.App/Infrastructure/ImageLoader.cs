using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace GamePriceHunter.App.Infrastructure;

/// <summary>
/// Attached property that loads an image URL asynchronously with a memory + disk cache:
/// &lt;Image infra:ImageLoader.Url="{Binding ImageUrl}" infra:ImageLoader.DecodeWidth="240" /&gt;.
/// Recycled list items are handled by checking the URL again before assigning the bitmap.
/// </summary>
public static class ImageLoader
{
    private const int MemoryCapacity = 500;
    private static readonly SemaphoreSlim DownloadGate = new(6);
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> MemoryIndex = [];
    private static readonly LinkedList<(string Key, BitmapSource Image)> MemoryOrder = new();
    private static readonly Dictionary<string, Task<byte[]?>> InFlight = [];
    private static readonly Lock Gate = new();
    private static HttpClient? _http;
    private static string? _diskDirectory;

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(ImageLoader), new PropertyMetadata(0));

    public static string? GetUrl(DependencyObject element) => (string?)element.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject element, string? value) => element.SetValue(UrlProperty, value);

    public static int GetDecodeWidth(DependencyObject element) => (int)element.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(DependencyObject element, int value) => element.SetValue(DecodeWidthProperty, value);

    public static void Initialize(HttpClient http, string diskDirectory)
    {
        _http = http;
        _diskDirectory = diskDirectory;
        Directory.CreateDirectory(diskDirectory);
        _ = Task.Run(() => PruneDiskCache(TimeSpan.FromDays(14)));
    }

    private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;

        image.Source = null;
        if (e.NewValue is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

        var width = GetDecodeWidth(image);
        var key = $"{width}|{url}";
        if (TryGetFromMemory(key, out var cached))
        {
            image.Source = cached;
            return;
        }

        _ = LoadAsync(image, url, key, width);
    }

    private static async Task LoadAsync(Image image, string url, string key, int width)
    {
        try
        {
            var bytes = await GetBytesAsync(url).ConfigureAwait(false);
            if (bytes is null)
                return;

            var bitmap = await Task.Run(() => Decode(bytes, width)).ConfigureAwait(false);
            if (bitmap is null)
                return;

            AddToMemory(key, bitmap);
            await image.Dispatcher.InvokeAsync(() =>
            {
                // The element may have been recycled for another item meanwhile.
                if (GetUrl(image) == url)
                    image.Source = bitmap;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken image is not worth surfacing; the placeholder stays.
        }
    }

    private static Task<byte[]?> GetBytesAsync(string url)
    {
        lock (Gate)
        {
            if (InFlight.TryGetValue(url, out var pending))
                return pending;
            var task = FetchAsync(url);
            InFlight[url] = task;
            _ = task.ContinueWith(_ => { lock (Gate) InFlight.Remove(url); }, TaskScheduler.Default);
            return task;
        }
    }

    private static async Task<byte[]?> FetchAsync(string url)
    {
        var path = _diskDirectory is null ? null : Path.Combine(_diskDirectory, Hash(url));
        if (path is not null && File.Exists(path))
        {
            try
            {
                return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        if (_http is null)
            return null;

        await DownloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024)
                return null;

            if (path is not null)
            {
                try
                {
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                }
                catch (IOException)
                {
                }
            }
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static BitmapSource? Decode(byte[] bytes, int width)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0)
                bitmap.DecodePixelWidth = width;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static bool TryGetFromMemory(string key, out BitmapSource image)
    {
        lock (Gate)
        {
            if (MemoryIndex.TryGetValue(key, out var node))
            {
                MemoryOrder.Remove(node);
                MemoryOrder.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null!;
        return false;
    }

    private static void AddToMemory(string key, BitmapSource image)
    {
        lock (Gate)
        {
            if (MemoryIndex.ContainsKey(key))
                return;
            MemoryIndex[key] = MemoryOrder.AddFirst((key, image));
            while (MemoryOrder.Count > MemoryCapacity && MemoryOrder.Last is { } last)
            {
                MemoryOrder.RemoveLast();
                MemoryIndex.Remove(last.Value.Key);
            }
        }
    }

    private static string Hash(string url) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

    private static void PruneDiskCache(TimeSpan maxAge)
    {
        if (_diskDirectory is null)
            return;
        try
        {
            var threshold = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(_diskDirectory))
            {
                if (File.GetLastWriteTimeUtc(file) < threshold)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
