namespace RemoteAdmin.Shared;

public interface IRemoteInputSink
{
    void Apply(InputEvent input);
    void ReleaseAll();
}

// Input is leased to exactly one existing, visible screen subscription.
public sealed class RemoteControlSession(ScreenShareSession screen, IRemoteInputSink sink,
    Func<ControlState, CancellationToken, Task> sendState, Action<string> log,
    TimeSpan? heartbeatTimeout = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private ControlSession? current;
    private Task monitor = Task.CompletedTask;
    private bool disposed;

    public async Task StartAsync(ControlRequest request, CancellationToken token)
    {
        ControlSession? session;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is not null) return;
            var access = screen.AccessFor(request.RequestId);
            session = access is null ? null : new ControlSession(request.RequestId, access,
                CancellationTokenSource.CreateLinkedTokenSource(token, access.Stopped));
            current = session;
            if (session is not null) monitor = MonitorAsync(session);
        }
        if (session is null)
            await sendState(new(request.RequestId, "unavailable", "Start a visible screen session before enabling control."), token);
    }

    public bool Apply(ControlInput request)
    {
        lock (gate)
        {
            var session = current;
            if (session is null || session.Id != request.RequestId || !session.Enabled ||
                session.Token.IsCancellationRequested || screen.AccessFor(request.RequestId) is null ||
                Environment.TickCount64 - session.LastHeartbeat > TimeoutMilliseconds || !RemoteInputRules.IsValid(request.Input))
                return false;
            try { sink.Apply(request.Input); return true; }
            catch (Exception ex)
            {
                log($"Remote input stopped: {ex.Message}");
                session.Stop.Cancel();
                ReleaseInputs();
                return false;
            }
        }
    }

    public void Heartbeat(string id)
    {
        lock (gate)
            if (current is { } session && session.Id == id && !session.Token.IsCancellationRequested)
                session.LastHeartbeat = Environment.TickCount64;
    }

    public async Task StopAsync(string id)
    {
        Task task;
        lock (gate)
        {
            if (current is not { } session || session.Id != id) return;
            session.Enabled = false;
            ReleaseInputs();
            session.Stop.Cancel();
            task = monitor;
        }
        await task;
    }

    private double TimeoutMilliseconds => (heartbeatTimeout ?? TimeSpan.FromMilliseconds(2500)).TotalMilliseconds;

    private async Task MonitorAsync(ControlSession session)
    {
        // Do not run UI or network continuations while StartAsync still holds the lock.
        await Task.Yield();
        using var release = session.Token.Register(() =>
        {
            lock (gate)
                if (ReferenceEquals(current, session)) { session.Enabled = false; ReleaseInputs(); }
        });
        try
        {
            await session.Access.Indicator.SetControlActiveAsync(true);
            session.Token.ThrowIfCancellationRequested();
            lock (gate) session.Enabled = screen.AccessFor(session.Id) is not null;
            if (!session.Enabled) return;
            await sendState(new(session.Id, "active", "Remote control active. Escape exits fullscreen; the red button stops sharing and control."), session.Token);
            while (!session.Token.IsCancellationRequested)
            {
                lock (gate)
                    if (screen.AccessFor(session.Id) is null || Environment.TickCount64 - session.LastHeartbeat > TimeoutMilliseconds)
                        break;
                await Task.Delay(50, session.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log($"Remote control ended: {ex.Message}"); }
        finally
        {
            lock (gate)
            {
                session.Enabled = false;
                if (ReferenceEquals(current, session)) ReleaseInputs();
            }
            try { await session.Access.Indicator.SetControlActiveAsync(false); }
            catch (Exception ex) { log($"Control indicator ended: {ex.Message}"); }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await sendState(new(session.Id, "stopped", "Remote control stopped."), timeout.Token);
            }
            catch (Exception ex) { log($"Control state could not be sent: {ex.Message}"); }
            lock (gate) { if (ReferenceEquals(current, session)) current = null; }
            session.Stop.Dispose();
        }
    }

    private void ReleaseInputs()
    {
        try { sink.ReleaseAll(); }
        catch (Exception ex) { log($"Input release failed: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Task task;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (current is { } session) { session.Enabled = false; ReleaseInputs(); session.Stop.Cancel(); }
            task = monitor;
        }
        await task;
    }

    private sealed class ControlSession(string id, ScreenShareSession.ScreenAccess access, CancellationTokenSource stop)
    {
        public string Id { get; } = id;
        public ScreenShareSession.ScreenAccess Access { get; } = access;
        public CancellationTokenSource Stop { get; } = stop;
        public CancellationToken Token { get; } = stop.Token;
        public long LastHeartbeat = Environment.TickCount64;
        public bool Enabled;
    }
}
