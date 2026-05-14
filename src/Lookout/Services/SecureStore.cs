using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Lookout.Services;

/// <summary>
/// Stores the Anthropic API key in Windows Credential Manager rather than
/// a plain file on disk.
/// </summary>
public static class SecureStore
{
    private const string TargetName = "Lookout:AnthropicApiKey";

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    public static void SaveApiKey(string apiKey)
    {
        var blob = Encoding.UTF8.GetBytes(apiKey);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetName,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "Lookout",
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException(
                    $"Failed to store API key (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static string? LoadApiKey()
    {
        if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
                return null;
            throw new InvalidOperationException($"Failed to read API key (Win32 error {err}).");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;

            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
            return Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void DeleteApiKey()
    {
        if (!CredDelete(TargetName, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND)
                throw new InvalidOperationException($"Failed to delete API key (Win32 error {err}).");
        }
    }

    public static bool HasApiKey() => !string.IsNullOrEmpty(LoadApiKey());
}
