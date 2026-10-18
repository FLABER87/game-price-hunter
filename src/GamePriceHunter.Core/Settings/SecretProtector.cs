using System.Runtime.InteropServices;
using System.Text;

namespace GamePriceHunter.Core.Settings;

/// <summary>
/// Encrypts secrets for the current Windows user with DPAPI (crypt32), so API keys in the
/// settings file are useless when copied to another account or machine.
/// </summary>
public static partial class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "GamePriceHunter.ApiKeys"u8.ToArray();

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
            return secret;
        var data = Transform(Encoding.UTF8.GetBytes(secret), protect: true);
        return Prefix + Convert.ToBase64String(data);
    }

    public static string? Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var data = Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var entropyBlob = new DataBlob();
        var outputBlob = new DataBlob();
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inputBlob.cbData = input.Length;
            inputBlob.pbData = inputHandle.AddrOfPinnedObject();
            entropyBlob.cbData = Entropy.Length;
            entropyBlob.pbData = entropyHandle.AddrOfPinnedObject();

            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref outputBlob);
            if (!ok)
                throw new InvalidOperationException($"DPAPI failed with error {Marshal.GetLastPInvokeError()}.");

            var output = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, output, 0, outputBlob.cbData);
            return output;
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
            if (outputBlob.pbData != IntPtr.Zero)
                LocalFree(outputBlob.pbData);
        }
    }

    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr handle);
}
