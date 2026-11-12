namespace RemoteAdmin.Shared;

// Screen sharing has no target-side indicator or local stop button. The Admin viewer owns
// the session lifetime: stopping it, closing it, or losing the connection ends sharing.
public sealed class ScreenShareSession(
    Func<string, long, CancellationToken, Task<ScreenFrame>> capture,
    Func<ScreenShareState, CancellationToken, Task> sendState,
    Func<ScreenFrame, CancellationToken, Task> sendFrame,
    Action<string> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? currentStop;
    private string? currentRequest;
    private Task currentTask = Task.CompletedTask;
    private bool disposed;
    private ScreenAccess? activeScreen;

    public bool IsActive(string requestId) => AccessFor(requestId) is not null;

    internal ScreenAccess? AccessFor(string requestId)
    {
        var access = Volatile.Read(ref activeScreen);
        return access is not null && access.RequestId == requestId && !access.Stopped.IsCancellationRequested
            ? access : null;
    }

    public async Task StartAsync(ScreenStart request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
            throw new ArgumentException("Invalid screen request.");
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!currentTask.IsCompleted)
            {
                if (currentRequest != request.RequestId)
                    await sendState(new(request.RequestId, "busy", "Another screen request is already active."), token);
                return;
            }
            currentStop?.Dispose();
            currentStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            currentRequest = request.RequestId;
            currentTask = RunAsync(request.RequestId, currentStop.Token);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(string requestId, CancellationToken token = default)
    {
        Task task = Task.CompletedTask;
        await gate.WaitAsync(token);
        try
        {
            if (currentRequest == requestId)
            {
                currentStop?.Cancel();
                task = currentTask;
            }
        }
        finally { gate.Release(); }
        await task;
    }

    private async Task RunAsync(string requestId, CancellationToken token)
    {
        var finalState = new ScreenShareState(requestId, "stopped", "Screen sharing stopped.");
        try
        {
            await sendState(new(requestId, "starting", "Starting screen sharing."), token);
            await using var sharing = new SharingLifetime(token);
            Volatile.Write(ref activeScreen, new(requestId, sharing.Token));
            await sendState(new(requestId, "streaming", "The primary display is being shared."), sharing.Token);
            long sequence = 0;
            var frameInterval = TimeSpan.FromSeconds(1d / Protocol.ScreenFramesPerSecond);
            using var pacing = new PeriodicTimer(frameInterval);
            while (!sharing.IsCancellationRequested)
            {
                var frame = await capture(requestId, ++sequence, sharing.Token);
                sharing.Token.ThrowIfCancellationRequested();
                await sendFrame(frame, sharing.Token);
                // Await each send: slow networks cannot queue an unbounded image backlog.
                // Periodic ticks compensate for OS timer jitter and coalesce missed
                // ticks. A slow capture/send still cannot create a frame backlog.
                if (!await pacing.WaitForNextTickAsync(sharing.Token)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log($"Screen sharing ended: {ex.Message}");
            finalState = new(requestId, "error", "Unable to share this desktop. Start a new request to try again.");
        }
        finally
        {
            Volatile.Write(ref activeScreen, null);
            // A stopped transport cannot leave an active capture session behind.
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await sendState(finalState, timeout.Token);
            }
            catch (Exception ex) { log($"Screen state could not be sent: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task task;
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            currentStop?.Cancel();
            task = currentTask;
        }
        finally { gate.Release(); }
        await task;
        currentStop?.Dispose();
    }

    internal sealed record ScreenAccess(string RequestId, CancellationToken Stopped);

    // Active capture sources must stop before the session is considered finished,
    // including when capture/encoding throws rather than a viewer stopping sharing.
    private sealed class SharingLifetime(CancellationToken first) : IAsyncDisposable
    {
        private readonly CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(first);
        public CancellationToken Token => source.Token;
        public bool IsCancellationRequested => source.IsCancellationRequested;
        public async ValueTask DisposeAsync()
        {
            try { await source.CancelAsync(); }
            finally { source.Dispose(); }
        }
    }
}
