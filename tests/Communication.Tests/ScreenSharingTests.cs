using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteAdmin.Shared;

internal static class ScreenSharingTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, int port,
        Func<Task<DeviceSnapshot[]?>> devices, Action<string> log)
    {
        await test("screen capture cannot start before the visible sharing indicator and pending requests can be cancelled", async () =>
        {
            var captureCount = 0;
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var states = new ConcurrentQueue<ScreenShareState>();
            await using var screen = new ScreenShareSession(async token =>
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }, (id, sequence, _) => { Interlocked.Increment(ref captureCount); return Task.FromResult(Frame(id, sequence)); },
                (state, _) => { states.Enqueue(state); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, log);
            await screen.StartAsync(new("pending"), CancellationToken.None);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(captureCount == 0, "Captured pixels before indicator");
            await screen.StopAsync("pending");
            Assert(captureCount == 0 && states.Last().Status == "stopped", "Cancelled request started capture");
        });

        await test("an unavailable screen indicator sends no frames", async () =>
        {
            var states = new ConcurrentQueue<ScreenShareState>();
            await using var screen = new ScreenShareSession(_ => Task.FromResult<IScreenShareLease?>(null),
                (_, _, _) => throw new Exception("Capture must not run without an indicator"),
                (state, _) => { states.Enqueue(state); return Task.CompletedTask; },
                (_, _) => throw new Exception("Frame must not be sent without an indicator"), log);
            await screen.StartAsync(new("unavailable"), CancellationToken.None);
            await Until(() => states.Any(x => x.Status == "unavailable"));
        });

        await test("local Stop revokes sharing, releases the indicator and prevents later frames", async () =>
        {
            var indicator = new Indicator();
            var frames = 0;
            await using var screen = Session(indicator, (_, _) => { Interlocked.Increment(ref frames); return Task.CompletedTask; }, log);
            await screen.StartAsync(new("local-stop"), CancellationToken.None);
            await Until(() => frames > 0);
            indicator.Stop.Cancel();
            await Until(() => indicator.Disposed);
            var previous = frames;
            await Task.Delay(300);
            Assert(frames == previous, "Pixels sent after local Stop");
        });

        await test("another viewer cannot stop or replace an active screen session", async () =>
        {
            var indicator = new Indicator();
            var frames = 0;
            await using var screen = Session(indicator, (_, _) => { Interlocked.Increment(ref frames); return Task.CompletedTask; }, log);
            await screen.StartAsync(new("owner"), CancellationToken.None);
            await Until(() => frames > 0);
            await screen.StartAsync(new("other"), CancellationToken.None);
            await screen.StopAsync("other");
            var before = frames;
            await Until(() => frames > before);
            Assert(!indicator.Disposed, "Another viewer stopped sharing");
            await screen.StopAsync("owner");
            Assert(indicator.Disposed, "Owner Stop did not release sharing");
        });

        await test("screen capture failure closes the indicator lease and reports an error", async () =>
        {
            var indicator = new Indicator();
            var states = new ConcurrentQueue<ScreenShareState>();
            await using var screen = new ScreenShareSession(_ => Task.FromResult<IScreenShareLease?>(indicator),
                (_, _, _) => throw new IOException("Synthetic locked desktop"),
                (state, _) => { states.Enqueue(state); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, log);
            await screen.StartAsync(new("failure"), CancellationToken.None);
            await Until(() => states.Any(x => x.Status == "error"));
            Assert(indicator.Disposed, "Failed capture retained the sharing indicator");
        });

        await test("a hidden indicator prevents a captured frame from being sent", async () =>
        {
            var indicator = new Indicator();
            var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = 0;
            await using var screen = new ScreenShareSession(_ => Task.FromResult<IScreenShareLease?>(indicator),
                async (id, sequence, token) =>
                {
                    capturing.TrySetResult();
                    await finish.Task.WaitAsync(token);
                    return Frame(id, sequence);
                }, (_, _) => Task.CompletedTask, (_, _) => { Interlocked.Increment(ref frames); return Task.CompletedTask; }, log);
            await screen.StartAsync(new("hidden"), CancellationToken.None);
            await capturing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            indicator.Visible = false;
            finish.SetResult();
            await Until(() => indicator.Disposed);
            Assert(frames == 0, "An image was sent while the indicator was hidden");
        });

        await test("slow screen sends do not queue captures and Stop cancels the blocked send", async () =>
        {
            var indicator = new Indicator();
            var captures = 0;
            var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var screen = new ScreenShareSession(_ => Task.FromResult<IScreenShareLease?>(indicator),
                (id, sequence, _) => { Interlocked.Increment(ref captures); return Task.FromResult(Frame(id, sequence)); },
                (_, _) => Task.CompletedTask, async (_, token) => { sending.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }, log);
            await screen.StartAsync(new("slow"), CancellationToken.None);
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(450);
            Assert(captures == 1, "Image backlog grew while a frame was unsent");
            await screen.StopAsync("slow").WaitAsync(TimeSpan.FromSeconds(2));
            Assert(indicator.Disposed, "Blocked send retained sharing after Stop");
        });

        await test("transport cancellation releases the local screen indicator", async () =>
        {
            using var stop = new CancellationTokenSource();
            var indicator = new Indicator();
            var frames = 0;
            await using var screen = Session(indicator, (_, _) => { Interlocked.Increment(ref frames); return Task.CompletedTask; }, log);
            await screen.StartAsync(new("disconnect"), stop.Token);
            await Until(() => frames > 0);
            stop.Cancel();
            await Until(() => indicator.Disposed);
        });

        await test("each new screen session opens a new visible sharing indicator", async () =>
        {
            var indicators = 0;
            var frames = 0;
            await using var screen = new ScreenShareSession(_ =>
            {
                Interlocked.Increment(ref indicators);
                return Task.FromResult<IScreenShareLease?>(new Indicator());
            }, (id, sequence, _) => Task.FromResult(Frame(id, sequence)), (_, _) => Task.CompletedTask,
                (_, _) => { Interlocked.Increment(ref frames); return Task.CompletedTask; }, log);
            await screen.StartAsync(new("first"), CancellationToken.None);
            await Until(() => frames > 0);
            await screen.StopAsync("first");
            var previous = frames;
            await screen.StartAsync(new("second"), CancellationToken.None);
            await Until(() => frames > previous);
            Assert(indicators == 2, "Previous indicator was reused");
            await screen.StopAsync("second");
        });

        await test("large screen frames go only to the requesting browser and its close stops sharing", async () =>
        {
            await using var peer = new ProbeClient(port, "screen-routing", "screen-routing", log, true);
            await peer.Ready;
            using var owner = new ClientWebSocket();
            using var spectator = new ClientWebSocket();
            var url = new Uri($"ws://127.0.0.1:{port}/ws/screen-routing");
            await owner.ConnectAsync(url, CancellationToken.None);
            await spectator.ConnectAsync(url, CancellationToken.None);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await Send(owner, new { type = "screenStart", payload = new { requestId = "spoofed" } });
            using var start = JsonDocument.Parse(await peer.Messages.Reader.ReadAsync(limit.Token));
            var requestId = start.RootElement.GetProperty("payload").GetProperty("requestId").GetString()!;
            Assert(requestId != "spoofed", "Browser chose its own screen routing ID");
            await peer.Send(new { type = "screenState", payload = new ScreenShareState(requestId, "streaming") });
            var largeFrame = Frame(requestId, 1) with { JpegBase64 = Convert.ToBase64String(new byte[130000]) };
            await peer.Send(new { type = "screenFrame", payload = largeFrame });
            Assert((await Receive(owner, limit.Token)).Contains("screenState"), "Owner did not receive screen state");
            var received = await Receive(owner, limit.Token);
            using var frameMessage = JsonDocument.Parse(received);
            Assert(frameMessage.RootElement.GetProperty("type").GetString() == "screenFrame" &&
                frameMessage.RootElement.GetProperty("payload").GetProperty("jpegBase64").GetString() == largeFrame.JpegBase64,
                "Large screen frame was lost or truncated");

            await Send(spectator, new { type = "screenStop", payload = new { requestId } });
            using var stop = JsonDocument.Parse(await peer.Messages.Reader.ReadAsync(limit.Token));
            Assert(stop.RootElement.GetProperty("payload").GetProperty("requestId").GetString() != requestId,
                "Spectator could stop the owner's subscription");
            using var noFrames = new CancellationTokenSource(200);
            try { await Receive(spectator, noFrames.Token); throw new Exception("Spectator received private screen data"); }
            catch (OperationCanceledException) when (noFrames.IsCancellationRequested) { }

            owner.Abort();
            using var closed = JsonDocument.Parse(await peer.Messages.Reader.ReadAsync(limit.Token));
            Assert(closed.RootElement.GetProperty("type").GetString() == "screenStop" &&
                closed.RootElement.GetProperty("payload").GetProperty("requestId").GetString() == requestId,
                "Closing viewer did not stop its screen session");
            Assert(!peer.Completion.IsCompleted, "Closing the viewer disconnected the client");
        });
        await UntilAsync(async () => (await devices())!.Length == 0);

        await test("old clients report screen sharing unsupported without receiving a screen command", async () =>
        {
            await using var peer = new ProbeClient(port, "old-screen-client", "old-screen-client", log);
            await peer.Ready;
            using var browser = new ClientWebSocket();
            await browser.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/old-screen-client"), CancellationToken.None);
            await Send(browser, new { type = "screenStart" });
            using var limit = new CancellationTokenSource(3000);
            var response = await Receive(browser, limit.Token);
            Assert(response.Contains("unsupported"), "Old client did not report missing screen support");
            Assert(!peer.Messages.Reader.TryRead(out _), "Screen command was sent to an unsupported client");
        });
        await UntilAsync(async () => (await devices())!.Length == 0);
    }

    private static ScreenShareSession Session(Indicator indicator, Func<ScreenFrame, CancellationToken, Task> send, Action<string> log) =>
        new(_ => Task.FromResult<IScreenShareLease?>(indicator),
            (id, sequence, _) => Task.FromResult(Frame(id, sequence)), (_, _) => Task.CompletedTask, send, log);
    private static ScreenFrame Frame(string id, long sequence) => new(id, 640, 360, "synthetic-test-pixels", sequence, DateTime.UtcNow);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> check) => await UntilAsync(() => Task.FromResult(check()));
    private static async Task UntilAsync(Func<Task<bool>> check)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 3000)
        {
            if (await check()) return;
            await Task.Delay(15);
        }
        throw new Exception("Screen test condition timed out");
    }
    private static async Task Send(WebSocket socket, object message) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Protocol.Json), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<string> Receive(WebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[65536];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer, token);
            if (frame.MessageType == WebSocketMessageType.Close) throw new Exception("Browser closed before receiving screen state");
            message.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }
    private sealed class Indicator : IScreenShareLease
    {
        public CancellationTokenSource Stop { get; } = new();
        public CancellationToken Revoked => Stop.Token;
        public volatile bool Visible = true;
        public bool IsVisible => Visible;
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; Stop.Dispose(); return ValueTask.CompletedTask; }
    }
}
