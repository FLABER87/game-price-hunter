namespace GamePriceHunter.Core.Updates;

/// <summary>Downloads a release installer and makes sure it is a Windows executable, not an error page.</summary>
public static class InstallerDownloader
{
    /// <summary>
    /// Saves <paramref name="url"/> to <paramref name="destinationPath"/>, reporting progress from 0 to 1.
    /// The file only appears under its final name once it is complete and valid.
    /// </summary>
    public static async Task<string> DownloadAsync(
        HttpClient http, Uri url, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var partial = destinationPath + ".part";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total > 0 && progress is not null)
                    {
                        // Whole percents only, so a large file doesn't flood the UI thread.
                        var percent = (int)(received * 100 / total.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(Math.Min(1.0, (double)received / total.Value));
                        }
                    }
                }
            }

            if (!IsWindowsExecutable(partial))
                throw new InvalidDataException("The downloaded file is not a Windows executable.");

            File.Move(partial, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    internal static bool IsWindowsExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[2];
        return stream.Length > 1024 && stream.Read(header) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
