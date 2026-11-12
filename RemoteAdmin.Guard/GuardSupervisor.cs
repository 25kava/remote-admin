using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace RemoteAdmin.Guard;

/// <summary>
/// Two-way watchdog. Each process runs one supervisor: c1 watches c2 and c2 watches c1.
/// A supervisor restores a missing partner from the backup kept by the other side, restarts a
/// stopped partner, and keeps both per-user startup entries present.
/// </summary>
public sealed class GuardSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);
    private const string UninstallResourceName = "RemoteAdmin.Guard.uninstall.bat";

    private readonly GuardRole role;
    private readonly Action<string> log;
    private readonly CancellationTokenSource shutdown;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;

    public GuardSupervisor(GuardRole role, Action<string>? log, CancellationTokenSource shutdown)
    {
        this.role = role;
        this.log = log ?? (_ => { });
        this.shutdown = shutdown;
    }

    public void Start() => loop ??= Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        var token = lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (IsDisabled())
                {
                    log("RemoteAdmin is disabled; stopping watchdog.");
                    shutdown.Cancel();
                    return;
                }

                EnsureDirectory(GuardPaths.LocalRoot, hide: false);
                EnsureDirectory(GuardPaths.C2Root, hide: true);
                EnsureStartupEntries();
                if (role == GuardRole.C1) EnsureUninstallScript();
                HealSibling();
            }
            catch (Exception ex)
            {
                log($"Watchdog: {ex.Message}");
            }

            try { await Task.Delay(TickInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void HealSibling()
    {
        var siblingPath = GuardPaths.SiblingPath(role);
        var backupPath = GuardPaths.SiblingBackupPath(role);

        if (!File.Exists(siblingPath))
        {
            if (!File.Exists(backupPath))
            {
                log($"Cannot restore {Path.GetFileName(siblingPath)}: no backup is available.");
                return;
            }

            CopyAtomic(backupPath, siblingPath);
            SetVisibility(siblingPath, hidden: role == GuardRole.C1);
            log($"Restored {Path.GetFileName(siblingPath)} from backup.");
        }
        else
        {
            RefreshBackup(siblingPath, backupPath);
        }

        if (!IsRunning(GuardPaths.SiblingMutexName(role)))
        {
            StartHidden(siblingPath);
            log($"Started {Path.GetFileName(siblingPath)}.");
        }
    }

    private void RefreshBackup(string sourcePath, string backupPath)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            var backup = new FileInfo(backupPath);
            if (backup.Exists && backup.Length == source.Length && backup.LastWriteTimeUtc >= source.LastWriteTimeUtc)
                return;

            CopyAtomic(sourcePath, backupPath);
            Hide(backupPath, system: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Unable to refresh the {Path.GetFileName(sourcePath)} backup: {ex.Message}");
        }
    }

    private static void CopyAtomic(string sourcePath, string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporaryPath, true);
            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private static bool IsRunning(string mutexName)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A handle exists but cannot be inspected from this context; treat it as alive.
            return true;
        }
    }

    private void StartHidden(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(path)!
            });
        }
        catch (Exception ex)
        {
            log($"Unable to start {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void EnsureDirectory(string path, bool hide)
    {
        Directory.CreateDirectory(path);
        if (!hide) return;

        var attributes = File.GetAttributes(path);
        var wanted = attributes | FileAttributes.Hidden | FileAttributes.System;
        if (attributes != wanted) File.SetAttributes(path, wanted);
    }

    private static void Hide(string path, bool system)
    {
        var attributes = File.GetAttributes(path);
        var wanted = attributes | FileAttributes.Hidden;
        if (system) wanted |= FileAttributes.System;
        if (attributes != wanted) File.SetAttributes(path, wanted);
    }

    private static void SetVisibility(string path, bool hidden)
    {
        var attributes = File.GetAttributes(path);
        var wanted = hidden
            ? attributes | FileAttributes.Hidden | FileAttributes.System
            : attributes & ~(FileAttributes.Hidden | FileAttributes.System);
        if (attributes != wanted) File.SetAttributes(path, wanted);
    }

    private static bool IsDisabled()
    {
        try
        {
            if (File.Exists(GuardPaths.DisabledFlagPath)) return true;
            using var key = Registry.CurrentUser.OpenSubKey(GuardPaths.RegistryKey);
            var value = key?.GetValue(GuardPaths.DisabledValue);
            return value is not null && Convert.ToInt32(value) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void EnsureStartupEntries()
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (run is null) return;

        SetRunValue(run, GuardPaths.C1RunValue, GuardPaths.C1Path);
        SetRunValue(run, GuardPaths.C2RunValue, GuardPaths.C2Path);
        run.DeleteValue(GuardPaths.LegacyRunValue, false);

        static void SetRunValue(RegistryKey key, string name, string path)
        {
            var value = $"\"{path}\"";
            if (!string.Equals(key.GetValue(name) as string, value, StringComparison.Ordinal))
                key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Keeps the visible uninstaller next to c1.exe and repairs it if it is removed.
    /// The installer embeds the same script from the installer folder.
    /// </summary>
    private static void EnsureUninstallScript()
    {
        if (File.Exists(GuardPaths.UninstallPath)) return;

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(UninstallResourceName);
        if (resource is null) return;

        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var wanted = buffer.ToArray();

        try
        {
            var temporaryPath = GuardPaths.UninstallPath + ".tmp";
            File.WriteAllBytes(temporaryPath, wanted);
            File.Move(temporaryPath, GuardPaths.UninstallPath, true);
        }
        catch (Exception)
        {
            // The uninstaller being temporarily locked must never break the client.
        }
    }
}
