namespace RemoteAdmin.Guard;

public enum GuardRole
{
    C1,
    C2
}

/// <summary>
/// Canonical installation layout shared by c1.exe, c2.exe, the installer and the uninstaller.
/// c1.exe lives in the visible RemoteAdmin folder; c2.exe lives in a hidden, system-marked
/// folder so the two binaries are never stored together.
/// </summary>
public static class GuardPaths
{
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAdmin");

    public static string C1Path => Path.Combine(LocalRoot, "c1.exe");
    public static string C2BackupPath => Path.Combine(LocalRoot, "c2.bak");
    public static string UninstallPath => Path.Combine(LocalRoot, "uninstall.bat");
    public static string DisabledFlagPath => Path.Combine(LocalRoot, "disabled.flag");

    public static string C2Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Caches", "RemoteAdminGuard");

    public static string C2Path => Path.Combine(C2Root, "c2.exe");
    public static string C1BackupPath => Path.Combine(C2Root, "c1.bak");

    public const string C1MutexName = "RemoteAdmin.C1";
    public const string C2MutexName = "RemoteAdmin.C2";

    public const string C1RunValue = "RemoteAdminC1";
    public const string C2RunValue = "RemoteAdminC2";
    public const string LegacyRunValue = "RemoteAdminClient";

    public const string RegistryKey = @"Software\RemoteAdminGuard";
    public const string DisabledValue = "Disabled";

    public static string PathFor(GuardRole role) => role == GuardRole.C1 ? C1Path : C2Path;
    public static string SiblingPath(GuardRole role) => role == GuardRole.C1 ? C2Path : C1Path;
    public static string SiblingBackupPath(GuardRole role) => role == GuardRole.C1 ? C2BackupPath : C1BackupPath;
    public static string OwnMutexName(GuardRole role) => role == GuardRole.C1 ? C1MutexName : C2MutexName;
    public static string SiblingMutexName(GuardRole role) => role == GuardRole.C1 ? C2MutexName : C1MutexName;
    public static string RunValueFor(GuardRole role) => role == GuardRole.C1 ? C1RunValue : C2RunValue;
}
