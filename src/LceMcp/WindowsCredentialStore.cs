using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LceMcp;

internal sealed class WindowsCredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string BuildImapTarget(string accountId) => $"lcemcp/imap/{accountId}";

    public void Write(string target, string username, string secret)
    {
        EnsureWindows();

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(secretBytes.Length);

        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = username
            };

            if (!CredWrite(ref credential, 0))
                ThrowLastWin32Error($"Failed to write credential '{target}'.");
        }
        finally
        {
            Array.Clear(secretBytes);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public string Read(string target)
    {
        EnsureWindows();

        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;

            throw new Win32Exception(error, $"Failed to read credential '{target}'.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return "";

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            try
            {
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public bool Exists(string target) => Read(target) is not null;

    public bool Delete(string target)
    {
        EnsureWindows();

        if (CredDelete(target, CredentialTypeGeneric, 0))
            return true;

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
            return false;

        throw new Win32Exception(error, $"Failed to delete credential '{target}'.");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This first slice stores secrets in Windows Credential Manager only.");
    }

    private static void ThrowLastWin32Error(string message)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
