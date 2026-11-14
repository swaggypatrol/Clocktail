using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using Clocktail.Domain.Profiles;

namespace Clocktail.Broker;

/// <summary>Durable, admin-owned receipts. Never repair/trust a pre-created user-writable approval directory.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProfileApprovals : IProfileApprovals
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private readonly string[] _directories;

    public WindowsProfileApprovals() : this(DefaultRoot(), CurrentSid()) { }

    /// <summary>The store under another root (the tests' scratch folder); the broker's is <see cref="DefaultRoot"/>.</summary>
    public WindowsProfileApprovals(string root, string sid)
    {
        _directories = Directories(root, sid);
        EnsureDirectories();
    }

    /// <summary>%ProgramData%\Clocktail: any account may create a folder under %ProgramData%, which is why one found
    /// there is trusted only by its owner and ACL, never by its name.</summary>
    public static string DefaultRoot()
    {
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        RejectLink(common);
        return Path.Combine(common, "Clocktail");
    }

    private static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("cannot identify the approval owner");
    }

    private static string[] Directories(string root, string sid)
    {
        string approvals = Path.Combine(root, "Approvals");
        return [root, approvals, Path.Combine(approvals, sid)];
    }

    /// <summary>
    /// The install's step, where elevation is certain and a person asked for it: each folder of the store that is
    /// missing is created protected, and one that is there but not to be trusted (another account made it first, a
    /// link, a file of that name) is moved aside under its own name plus ".untrusted-…" and a protected one made in
    /// its place. Nothing in a folder moved aside is read: its receipts are never taken for approvals, so every
    /// profile is confirmed again. A trusted folder is left exactly as it is. Throws when a folder can be neither
    /// moved nor made; the broker then starts without approvals (<see cref="UnavailableProfileApprovals"/>).
    /// </summary>
    public static void Repair(Action<string> say) => Repair(DefaultRoot(), CurrentSid(), say);

    public static void Repair(string root, string sid, Action<string> say)
    {
        foreach (string path in Directories(root, sid))
        {
            if (Exists(path, out bool isDirectory))
            {
                if (isDirectory && Trusted(path)) continue;
                string aside = $"{path}.untrusted-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("n")[..8]}";
                if (isDirectory) Directory.Move(path, aside); else File.Move(path, aside);
                say($"approval folder {path} was not admin-owned and protected: moved aside to {aside}; nothing in it is used, so each daily profile is confirmed once more");
            }
            CreateSecureDirectory(path);
            if (!Trusted(path))
                throw new UnauthorizedAccessException($"approval directory could not be made admin-owned and protected: {path}");
        }
    }

    /// <summary>Whether anything bears the name, a link included (its target is never looked at).</summary>
    private static bool Exists(string path, out bool isDirectory)
    {
        isDirectory = false;
        try
        {
            var attributes = File.GetAttributes(path);
            isDirectory = (attributes & FileAttributes.Directory) != 0;
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static bool Trusted(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                && IsTrustedSecurity(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    // Public so security descriptors can be checked without touching ProgramData or elevating tests.
    public static DirectorySecurity DirectoryPermissions()
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(Administrators); // same-user owner would retain WRITE_DAC even without an allow ACE
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, SystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    public static FileSecurity FilePermissions()
    {
        var acl = new FileSecurity();
        acl.SetOwner(Administrators);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, SystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }

    public static bool IsTrustedSecurity(FileSystemSecurity acl)
    {
        if (!acl.AreAccessRulesProtected || acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || (!owner.Equals(Administrators) && !owner.Equals(SystemSid))) return false;
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count == 2 && rules.All(r => !r.IsInherited && r.AccessControlType == AccessControlType.Allow
                && r.FileSystemRights == FileSystemRights.FullControl
                && (r.IdentityReference.Equals(Administrators) || r.IdentityReference.Equals(SystemSid)))
            && rules.Select(r => r.IdentityReference.Value).Distinct().Count() == 2;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException($"approval path must not be a junction/symlink: {path}");
    }

    private void EnsureDirectories()
    {
        foreach (string path in _directories)
        {
            CreateSecureDirectory(path);
            RejectLink(path);
            if (!IsTrustedSecurity(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)))
                throw new UnauthorizedAccessException($"approval directory is not admin-owned and protected: {path}; repair it explicitly as administrator, never import its existing receipts automatically");
        }
    }

    private static unsafe void CreateSecureDirectory(string path)
    {
        byte[] descriptor = DirectoryPermissions().GetSecurityDescriptorBinaryForm();
        fixed (byte* security = descriptor)
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = (nint)security,
            };
            // Native create is atomic and NEVER changes an existing directory's owner/DACL. In particular,
            // an attacker winning a pre-creation race is rejected by validation, not "repaired" into trust.
            if (!CreateDirectory(path, ref attributes))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 183) // ERROR_ALREADY_EXISTS: validate the original ACL next
                    throw new IOException($"cannot create protected approval directory: {path}",
                        new System.ComponentModel.Win32Exception(error));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);

    private string ReceiptPath(string gpu, string slot)
    {
        EnsureDirectories();
        // Neither GPU ids nor user-supplied preset names become path components.
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new[] { gpu.ToUpperInvariant(), slot }))));
        return Path.Combine(_directories[^1], key + ".sha256");
    }

    private static void ValidateFile(string path)
    {
        RejectLink(path);
        if (!IsTrustedSecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)))
            throw new UnauthorizedAccessException("approval receipt has unsafe ownership or ACL");
    }

    public bool Matches(string gpu, string slot, DailyProfile profile)
    {
        string path = ReceiptPath(gpu, slot);
        if (!File.Exists(path)) return false; // no migration by trusting the user's existing JSON
        ValidateFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length != 64) return false;
        Span<byte> bytes = stackalloc byte[64];
        file.ReadExactly(bytes);
        return Encoding.ASCII.GetString(bytes) == ProfileApproval.Fingerprint(gpu, slot, profile);
    }

    public void Remember(string gpu, string slot, DailyProfile profile)
    {
        string path = ReceiptPath(gpu, slot);
        if (File.Exists(path)) ValidateFile(path);
        string temporary = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            using (var file = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.WriteThrough, FilePermissions()))
            {
                file.Write(Encoding.ASCII.GetBytes(ProfileApproval.Fingerprint(gpu, slot, profile)));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true); // replacement retains the temp file's protected ACL/owner
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Forget(string gpu, string slot)
    {
        string path = ReceiptPath(gpu, slot);
        if (File.Exists(path)) { ValidateFile(path); File.Delete(path); }
    }
}
