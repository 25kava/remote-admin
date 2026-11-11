namespace RemoteAdmin.Shared;

public static class ConnectionSupervisor
{
    public static async Task RunAsync(
        Func<CancellationToken, Task<List<DiscoveryAnnouncement>>> discover,
        Func<DiscoveryAnnouncement, CancellationToken, Task> connect,
        Action<string> log, CancellationToken token, TimeSpan? scanInterval = null)
    {
        var sessions = new Dictionary<string, Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var group in (await discover(token)).GroupBy(x => x.InstanceId))
                    {
                        if (sessions.TryGetValue(group.Key, out var session) && !session.IsCompleted) continue;
                        sessions[group.Key] = ConnectCandidates(group.ToArray(), connect, log, token);
                    }
                    foreach (var id in sessions.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray())
                        sessions.Remove(id);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { log($"Discovery failed: {ex.Message}"); }
                await Task.Delay(scanInterval ?? TimeSpan.FromSeconds(3), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(sessions.Values); }
    }

    private static async Task ConnectCandidates(DiscoveryAnnouncement[] candidates,
        Func<DiscoveryAnnouncement, CancellationToken, Task> connect, Action<string> log, CancellationToken token)
    {
        foreach (var server in candidates)
        {
            if (token.IsCancellationRequested) break;
            try { await connect(server, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { log($"Connection to {server.Host}:{server.Port} failed: {ex.Message}"); }
        }
    }
}
