using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DeveloperPresence.Core.Credentials;

/// <summary>
/// Platform credential store backed by Windows Credential Manager (CredWriteW / CredReadW / CredDeleteW).
/// Strictly Windows-only. Core tests and non-Windows environments use explicit test fakes (InMemoryCredentialStore).
/// </summary>
public sealed class WindowsCredentialManager : ICredentialStore
{
    private static void EnsureWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only supported on Windows.");
        }
    }

    public Task SaveCredentialAsync(string target, string username, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(secret);

        EnsureWindows();

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        IntPtr credentialBlobPtr = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, credentialBlobPtr, secretBytes.Length);

            var credential = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlob = credentialBlobPtr,
                CredentialBlobSize = (uint)secretBytes.Length,
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                UserName = username
            };

            if (!NativeMethods.CredWriteW(ref credential, 0))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Failed to write credential to Windows Credential Manager (error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(credentialBlobPtr);
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadCredentialAsync(string target, string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        EnsureWindows();

        if (!NativeMethods.CredReadW(target, NativeMethods.CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ERROR_NOT_FOUND)
            {
                return Task.FromResult<string?>(null);
            }
            throw new Win32Exception(error, $"Failed to read credential from Windows Credential Manager (error {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            byte[] buffer = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, buffer, 0, buffer.Length);
            string secret = Encoding.UTF8.GetString(buffer);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            NativeMethods.CredFree(credPtr);
        }
    }

    public Task DeleteCredentialAsync(string target, string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        EnsureWindows();

        if (!NativeMethods.CredDeleteW(target, NativeMethods.CRED_TYPE_GENERIC, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_NOT_FOUND)
            {
                throw new Win32Exception(error, $"Failed to delete credential from Windows Credential Manager (error {error}).");
            }
        }

        return Task.CompletedTask;
    }

    private static class NativeMethods
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        public const int ERROR_NOT_FOUND = 1168;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWriteW([In] ref CREDENTIAL UserCredential, [In] uint Flags);

        [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredReadW(string TargetName, uint Type, uint ReservedFlag, out IntPtr Credential);

        [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDeleteW(string TargetName, uint Type, uint Flags);

        [DllImport("Advapi32.dll", SetLastError = true, EntryPoint = "CredFree")]
        public static extern void CredFree([In] IntPtr Buffer);
    }
}
