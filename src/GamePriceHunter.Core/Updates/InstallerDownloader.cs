using System.Net;

namespace GamePriceHunter.Core.Updates;

/// <summary>
/// Downloads a release installer and makes sure it is the one the release key signed, not an error page
/// or a replaced file.
/// </summary>
public static class InstallerDownloader
{
    /// <summary>
    /// Saves the installer of <paramref name="release"/> to <paramref name="destinationPath"/>, reporting progress
    /// from 0 to 1. The file only appears under its final name once it is complete and its signature (see
    /// <see cref="UpdateSignature"/>) matches; otherwise this throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static Task<string> DownloadAsync(
        HttpClient http, ReleaseInfo release, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        DownloadAsync(http, release, destinationPath, UpdateSignature.ReleasePublicKey, progress, cancellationToken);

    internal static async Task<string> DownloadAsync(
        HttpClient http, ReleaseInfo release, string destinationPath, string publicKey, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // The signature first: an unsigned release is not worth downloading. Releases before signing
        // started (0.2.0 and older) never get here, as the app only installs versions newer than itself.
        var signature = await DownloadSignatureAsync(http, release, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var partial = destinationPath + ".part";
        try
        {
            using (var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
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
            if (!await UpdateSignature.VerifyAsync(partial, release.Tag, signature, publicKey, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException($"The installer of {release.Tag} does not match its signature, so it is not the file the release was built with.");

            File.Move(partial, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static async Task<string> DownloadSignatureAsync(HttpClient http, ReleaseInfo release, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(release.SignatureUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException($"Release {release.Tag} has no signature ({release.SignatureUrl}), so its installer can't be checked.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
