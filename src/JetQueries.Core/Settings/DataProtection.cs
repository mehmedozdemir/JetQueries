using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace JetQueries.Core.Settings;

/// <summary>
/// Encrypts data for the current user of this machine, for files that may hold secrets the user typed (the
/// text of open query tabs: <c>CREATE USER … IDENTIFIED BY …</c>).
/// </summary>
public interface ILocalDataProtector
{
    /// <summary>Encrypts <paramref name="data"/>.</summary>
    byte[] Protect(byte[] data);

    /// <summary>Decrypts what <see cref="Protect"/> wrote.</summary>
    /// <exception cref="CryptographicException">The data was written by another user or machine, or is damaged.</exception>
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// <see cref="ILocalDataProtector"/> on the Windows Data Protection API (DPAPI), for the current user: only that
/// Windows account on this machine can read the files back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDataProtector : ILocalDataProtector
{
    private const int UiForbidden = 0x1;

    // Extra entropy: another program running as the same user cannot decrypt the files with a plain call.
    private static readonly byte[] Entropy = "JetQueries.Settings.v1"u8.ToArray();

    /// <inheritdoc />
    public byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Transform(data, protect: true);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Transform(data, protect: false);
    }

    private static unsafe byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            var outBlob = default(DataBlob);
            var ok = protect
                ? CryptProtectData(&inBlob, null, &entropyBlob, 0, 0, UiForbidden, &outBlob)
                : CryptUnprotectData(&inBlob, 0, &entropyBlob, 0, 0, UiForbidden, &outBlob);
            if (!ok)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new CryptographicException(new Win32Exception(error).Message);
            }

            try
            {
                return new Span<byte>((void*)outBlob.Data, outBlob.Size).ToArray();
            }
            finally
            {
                // The plain text of an unprotected buffer is not left for LocalFree to drop as it is.
                new Span<byte>((void*)outBlob.Data, outBlob.Size).Clear();
                LocalFree(outBlob.Data);
            }
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptProtectData(DataBlob* dataIn, string? description, DataBlob* entropy, nint reserved, nint prompt, int flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptUnprotectData(DataBlob* dataIn, nint description, DataBlob* entropy, nint reserved, nint prompt, int flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    // DATA_BLOB
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }
}
