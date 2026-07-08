using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Lookout.Services;

/// <summary>
/// Stores API keys in Windows Credential Manager rather than a plain file on
/// disk. Each logical key (per provider) is a separate credential entry.
/// </summary>
public static class SecureStore
{
    /// <summary>Credential account names, one per provider.</summary>
    public const string AnthropicAccount = "AnthropicApiKey";
    public const string OpenRouterAccount = "OpenRouterApiKey";

    private const string TargetPrefix = "Lookout:";

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

    /// <summary>Stores a value under the given account (e.g. a provider key name).</summary>
    public static void Save(string account, string value)
    {
        var blob = Encoding.UTF8.GetBytes(value);
        var blobPtr = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetPrefix + account,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "Lookout",
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException(
                    $"Failed to store credential (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>Loads the value for the given account, or null if not set.</summary>
    public static string? Load(string account)
    {
        if (!CredRead(TargetPrefix + account, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
                return null;
            throw new InvalidOperationException($"Failed to read credential (Win32 error {err}).");
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

    /// <summary>Deletes the value for the given account. No-op if absent.</summary>
    public static void Delete(string account)
    {
        if (!CredDelete(TargetPrefix + account, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND)
                throw new InvalidOperationException($"Failed to delete credential (Win32 error {err}).");
        }
    }

    public static bool Has(string account) => !string.IsNullOrEmpty(Load(account));

    // --- Backward-compatible Anthropic-key helpers ------------------------

    public static void SaveApiKey(string apiKey) => Save(AnthropicAccount, apiKey);

    public static string? LoadApiKey() => Load(AnthropicAccount);

    public static void DeleteApiKey() => Delete(AnthropicAccount);

    public static bool HasApiKey() => Has(AnthropicAccount);
}
