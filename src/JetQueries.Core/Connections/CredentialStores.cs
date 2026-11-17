using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace JetQueries.Core.Connections;

/// <summary>
/// <see cref="ICredentialStore"/> on Windows Credential Manager. Entries are generic credentials named
/// <c>JetQueries/connection/{id}</c>, encrypted by Windows for the current user and kept on this machine only
/// (they do not roam). They are visible, and removable, under "Windows Credentials" in Control Panel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    // CRED_MAX_CREDENTIAL_BLOB_SIZE is 5 * 512 bytes; passwords are stored as UTF-16.
    private const int MaxPasswordChars = 5 * 512 / 2;

    private readonly string _prefix;

    /// <summary>Creates the store.</summary>
    /// <param name="targetPrefix">Prefix of the credential names; tests pass their own to stay out of the real entries.</param>
    public WindowsCredentialStore(string targetPrefix = "JetQueries/connection/")
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPrefix);
        _prefix = targetPrefix;
    }

    /// <inheritdoc />
    public bool IsPersistent => true;

    /// <inheritdoc />
    public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.Run(() => Read(Target(connectionId)), cancellationToken);

    /// <inheritdoc />
    public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > MaxPasswordChars)
        {
            throw new ArgumentException($"Windows Credential Manager stores at most {MaxPasswordChars} characters.", nameof(password));
        }

        return Task.Run(() => Write(Target(connectionId), password), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeletePasswordAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                if (!CredDelete(Target(connectionId), CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() is var error and not ErrorNotFound)
                {
                    throw new Win32Exception(error);
                }
            },
            cancellationToken);

    private string Target(Guid connectionId) => _prefix + connectionId.ToString("D");

    private static unsafe string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = (Credential*)pointer;
            return credential->CredentialBlob == nint.Zero
                ? string.Empty
                : Encoding.Unicode.GetString((byte*)credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static unsafe void Write(string target, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        try
        {
            fixed (byte* blobPointer = blob)
            fixed (char* targetPointer = target)
            fixed (char* userPointer = "JetQueries")
            {
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = (nint)targetPointer,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (nint)blobPointer,
                    Persist = CredPersistLocalMachine,
                    UserName = (nint)userPointer,
                };
                if (!CredWrite(&credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
        }
        finally
        {
            // Do not leave a copy of the password in a buffer waiting for the garbage collector.
            Array.Clear(blob);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredWrite(Credential* credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint buffer);

    // CREDENTIALW
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }
}

/// <summary>
/// Fallback <see cref="ICredentialStore"/> for platforms without a supported protected store yet: passwords are
/// held in memory for the session and never written anywhere.
/// </summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<Guid, string> _passwords = new();

    /// <inheritdoc />
    public bool IsPersistent => false;

    /// <inheritdoc />
    public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(_passwords.GetValueOrDefault(connectionId));

    /// <inheritdoc />
    public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        _passwords[connectionId] = password;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeletePasswordAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        _passwords.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }
}
