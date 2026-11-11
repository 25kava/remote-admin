namespace RemoteAdmin.Shared;

public interface IScreenShareLease : IAsyncDisposable
{
    CancellationToken Revoked { get; }
    bool IsVisible { get; }
}

// Capture is only invoked after a visible local sharing indicator has opened.
public sealed class ScreenShareSession(
    Func<CancellationToken, Task<IScreenShareLease?>> openIndicator,
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
            await sendState(new(requestId, "starting", "Opening the sharing indicator on the client computer."), token);
            await using var indicator = await openIndicator(token);
            token.ThrowIfCancellationRequested();
            if (indicator is null)
            {
                finalState = new(requestId, "unavailable", "The sharing indicator could not open, or another Admin is already viewing this screen.");
                return;
            }
            using var sharing = CancellationTokenSource.CreateLinkedTokenSource(token, indicator.Revoked);
            await sendState(new(requestId, "streaming", "The primary display is being shared. The client has a visible Stop sharing button."), sharing.Token);
            long sequence = 0;
            while (!sharing.IsCancellationRequested)
            {
                if (!indicator.IsVisible) throw new InvalidOperationException("The screen sharing indicator is no longer visible.");
                var started = Environment.TickCount64;
                var frame = await capture(requestId, ++sequence, sharing.Token);
                sharing.Token.ThrowIfCancellationRequested();
                if (!indicator.IsVisible) throw new InvalidOperationException("The screen sharing indicator is no longer visible.");
                await sendFrame(frame, sharing.Token);
                // Await each send: slow networks cannot queue an unbounded image backlog.
                var delay = 200 - (Environment.TickCount64 - started);
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), sharing.Token);
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
            // A stopped transport cannot keep the local sharing indicator alive.
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
}
