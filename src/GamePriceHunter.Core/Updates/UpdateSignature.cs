using System.Security.Cryptography;
using System.Text;

namespace GamePriceHunter.Core.Updates;

/// <summary>
/// ECDSA P-256 signature that vouches for a release installer. CI signs the installer of every tag with the
/// private key in the UPDATE_SIGNING_KEY secret and attaches the signature next to it as "&lt;installer&gt;.sig"
/// (Base64). The app runs a downloaded installer only when that signature checks out against
/// <see cref="ReleasePublicKey"/>.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Public half of the release signing key (SubjectPublicKeyInfo, Base64); build\new-signing-key.ps1 writes it.</summary>
    public const string ReleasePublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYHo51tFFmGB2fvScu1aQwhha192fsZeZV4akXuPRWDXxeMYTp/CMr3JQPDj0JCoXf0kL7LCR/SJb/n9uiZx3Jw==";

    public const string FileExtension = ".sig";

    private const int SignatureLength = 64;

    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Signs the installer at <paramref name="path"/> for the release <paramref name="tag"/> ("v1.2.3").</summary>
    public static async Task<string> SignAsync(string path, string tag, ECDsa privateKey, CancellationToken cancellationToken = default)
    {
        var message = await CreateMessageAsync(path, tag, cancellationToken).ConfigureAwait(false);
        return Convert.ToBase64String(privateKey.SignData(message, HashAlgorithmName.SHA256));
    }

    /// <summary>True when <paramref name="signature"/> was made by the release key for this file and this tag.</summary>
    public static Task<bool> VerifyAsync(string path, string tag, string signature, CancellationToken cancellationToken = default) =>
        VerifyAsync(path, tag, signature, ReleasePublicKey, cancellationToken);

    internal static async Task<bool> VerifyAsync(string path, string tag, string signature, string publicKey, CancellationToken cancellationToken)
    {
        var bytes = new byte[SignatureLength];
        if (!Convert.TryFromBase64String(signature.Trim(), bytes, out var written) || written != SignatureLength)
            return false;

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        var message = await CreateMessageAsync(path, tag, cancellationToken).ConfigureAwait(false);
        return key.VerifyData(message, bytes, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// The signed text names the tag as well as the file's SHA-256, so the signature of an older release
    /// can't pass for the latest one.
    /// </summary>
    private static async Task<byte[]> CreateMessageAsync(string path, string tag, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetBytes($"Game Price Hunter update\n{tag}\n{Convert.ToHexStringLower(hash)}\n");
    }
}
