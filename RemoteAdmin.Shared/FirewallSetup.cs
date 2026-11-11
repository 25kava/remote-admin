using System.Diagnostics;
using System.Security.Principal;

namespace RemoteAdmin.Shared;

public static class FirewallSetup
{
    private const string AdminDiscoveryRule = "Remote Admin - Discovery (UDP 38991)";
    private const string AdminClientRule = "Remote Admin - Client connections (TCP 38992)";
    private const string ClientWebSocketRule = "Remote Admin Client - WebSocket outbound (TCP 38992)";
    private const string ClientDiscoveryRule = "Remote Admin Client - Discovery outbound (UDP 38991)";

    public static bool HandleElevatedCommand(string[] args, string executablePath, Action<string> log)
    {
        var roleIndex = Array.IndexOf(args, "--firewall-role");
        if (!args.Contains("--configure-firewall", StringComparer.OrdinalIgnoreCase) || roleIndex < 0 || roleIndex + 1 >= args.Length)
            return false;

        var role = args[roleIndex + 1];
        var success = role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            ? ConfigureAdmin(executablePath, log)
            : role.Equals("client", StringComparison.OrdinalIgnoreCase) && ConfigureClient(executablePath, log);
        Environment.ExitCode = success ? 0 : 1;
        return true;
    }

    public static bool EnsureAdmin(string executablePath, Action<string> log) =>
        Ensure(executablePath, "admin", [AdminDiscoveryRule, AdminClientRule], log);

    public static bool EnsureClient(string executablePath, Action<string> log) =>
        Ensure(executablePath, "client", [ClientWebSocketRule, ClientDiscoveryRule], log);

    private static bool Ensure(string executablePath, string role, string[] ruleNames, Action<string> log)
    {
        if (ruleNames.All(RuleExists))
            return true;

        if (!IsAdministrator())
        {
            log("Firewall permissions are missing; requesting administrator approval.");
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = $"--configure-firewall --firewall-role {role}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                });
                process?.WaitForExit();
                return process?.ExitCode == 0 && ruleNames.All(RuleExists);
            }
            catch (Exception ex)
            {
                log($"Firewall permission request failed: {ex.Message}");
                return false;
            }
        }

        return role == "admin" ? ConfigureAdmin(executablePath, log) : ConfigureClient(executablePath, log);
    }

    private static bool ConfigureAdmin(string executablePath, Action<string> log) =>
        ReplaceRules([
            ["name=" + AdminDiscoveryRule, "dir=in", "action=allow", "protocol=UDP", "localport=38991", "program=" + executablePath, "profile=any", "enable=yes"],
            ["name=" + AdminClientRule, "dir=in", "action=allow", "protocol=TCP", "localport=38992", "program=" + executablePath, "profile=any", "enable=yes"]
        ], [AdminDiscoveryRule, AdminClientRule], log);

    private static bool ConfigureClient(string executablePath, Action<string> log) =>
        ReplaceRules([
            ["name=" + ClientWebSocketRule, "dir=out", "action=allow", "protocol=TCP", "remoteport=38992", "program=" + executablePath, "profile=any", "enable=yes"],
            ["name=" + ClientDiscoveryRule, "dir=out", "action=allow", "protocol=UDP", "remoteport=38991", "program=" + executablePath, "profile=any", "enable=yes"]
        ], [ClientWebSocketRule, ClientDiscoveryRule], log);

    private static bool ReplaceRules(string[][] rules, string[] names, Action<string> log)
    {
        try
        {
            foreach (var name in names)
                RunNetsh(["advfirewall", "firewall", "delete", "rule", "name=" + name], false);
            foreach (var rule in rules)
                if (RunNetsh(["advfirewall", "firewall", "add", "rule", .. rule], true) != 0)
                    return false;
            return names.All(RuleExists);
        }
        catch (Exception ex)
        {
            log($"Firewall rule setup failed: {ex.Message}");
            return false;
        }
    }

    private static bool RuleExists(string name) => RunNetsh(["advfirewall", "firewall", "show", "rule", "name=" + name], false) == 0;

    private static int RunNetsh(string[] arguments, bool logOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            RedirectStandardOutput = logOutput,
            RedirectStandardError = logOutput,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) return -1;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool IsAdministrator() =>
        OperatingSystem.IsWindows() &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
