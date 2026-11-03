using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace AICollaborationPlatform.App.Storage;

/// <summary>
/// Windows Credential Manager ("Control Panel → Credential Manager → Windows Credentials").
/// Secrets are encrypted by Windows for the signed-in user and never touch the app's files.
/// Uses P/Invoke to call the Win32 Cred* functions in advapi32.dll directly.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2; // survives sign-out and reboot; not synced to other PCs
    private const int ErrorNotFound = 1168;
    private const int MaxSecretBytes = 5 * 512; // CRED_MAX_CREDENTIAL_BLOB_SIZE

    public string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw new Win32Exception(error, $"Couldn't read '{target}' from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlobSize == 0)
            {
                return "";
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public void Write(string target, string userName, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxSecretBytes)
        {
            throw new ArgumentException($"The secret is too long for Windows Credential Manager (max {MaxSecretBytes / 2} characters).", nameof(secret));
        }

        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(userName);
        var commentPtr = Marshal.StringToCoTaskMemUni("Saved by AI Collaboration Platform");
        var blobPtr = Marshal.AllocCoTaskMem(Math.Max(bytes.Length, 1));

        try
        {
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);

            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                UserName = userPtr,
                Comment = commentPtr,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CredPersistLocalMachine,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't save '{target}' to Windows Credential Manager.");
            }
        }
        finally
        {
            // Wipe our copies of the secret before releasing the memory.
            Marshal.Copy(new byte[bytes.Length], 0, blobPtr, bytes.Length);
            Array.Clear(bytes);
            Marshal.FreeCoTaskMem(blobPtr);
            Marshal.FreeCoTaskMem(commentPtr);
            Marshal.FreeCoTaskMem(userPtr);
            Marshal.FreeCoTaskMem(targetPtr);
        }
    }

    public bool Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound ? false : throw new Win32Exception(error, $"Couldn't remove '{target}' from Windows Credential Manager.");
    }

    // Mirrors the Win32 CREDENTIALW struct. Strings are IntPtr so one layout works for both reading and writing.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
