using System.Collections.Concurrent;
using System.Diagnostics;
using RemoteAdmin.Shared;

internal static class RemoteControlTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<string> log)
    {
        await test("remote input requires an active screen owned by the same request", async () =>
        {
            var sink = new Sink();
            var states = new ConcurrentQueue<ControlState>();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log);
            Assert(!control.Apply(new("owner", Key("keyDown", "KeyA"))), "Input ran before starting a screen session");
            await control.StartAsync(new("owner"), CancellationToken.None);
            Assert(states.Last().Status == "unavailable", "Control started without a live screen");
            await screen.StartAsync(new("owner"), CancellationToken.None);
            await Until(() => screen.IsActive("owner"));
            await control.StartAsync(new("other"), CancellationToken.None);
            Assert(states.Last().Status == "unavailable", "Another viewer took control");
            await control.StartAsync(new("owner"), CancellationToken.None);
            await Until(() => states.Any(x => x.Status == "active"));
            Assert(!control.Apply(new("other", Key("keyDown", "KeyA"))), "Input from the wrong viewer was accepted");
            Assert(control.Apply(new("owner", Key("keyDown", "ControlLeft"))), "Owner input was rejected");
            await control.StopAsync("other");
            Assert(sink.Held == 1, "A different viewer stopped control");
            await control.StopAsync("owner");
            Assert(sink.Held == 0, "Stop did not release input");
            Assert(!control.Apply(new("owner", Key("keyDown", "KeyA"))), "Input ran after Stop");
        });

        await test("stopping screen sharing releases held mouse buttons and keys", async () =>
        {
            var sink = new Sink(); var states = new ConcurrentQueue<ControlState>();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log);
            await Start(screen, control, states);
            control.Apply(new("owner", Key("keyDown", "ShiftLeft")));
            control.Apply(new("owner", new("buttonDown", .4, .6, 0, 0, null)));
            Assert(sink.Held == 2, "Test did not hold a key and mouse button");
            await screen.StopAsync("owner");
            await Until(() => sink.Held == 0 && states.Any(x => x.Status == "stopped"));
            Assert(!control.Apply(new("owner", Key("keyDown", "KeyB"))), "Input continued after screen sharing stopped");
        });

        await test("control heartbeat expiry releases input while view-only streaming can continue", async () =>
        {
            var sink = new Sink(); var states = new ConcurrentQueue<ControlState>();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log, TimeSpan.FromMilliseconds(180));
            await Start(screen, control, states);
            control.Apply(new("owner", Key("keyDown", "ControlLeft")));
            await Until(() => states.Any(x => x.Status == "stopped"));
            Assert(sink.Held == 0 && screen.IsActive("owner"), "Heartbeat timeout retained keys or stopped viewing");
        });

        await test("valid control heartbeats renew the session but another viewer cannot renew it", async () =>
        {
            var sink = new Sink(); var states = new ConcurrentQueue<ControlState>();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log, TimeSpan.FromMilliseconds(200));
            await Start(screen, control, states);
            for (var i = 0; i < 5; i++) { await Task.Delay(70); control.Heartbeat("owner"); }
            Assert(!states.Any(x => x.Status == "stopped"), "Owner heartbeats did not renew control");
            for (var i = 0; i < 5; i++) { await Task.Delay(70); control.Heartbeat("other"); }
            await Until(() => states.Any(x => x.Status == "stopped"));
        });

        await test("transport cancellation rejects further remote input", async () =>
        {
            var sink = new Sink(); var states = new ConcurrentQueue<ControlState>();
            using var stop = new CancellationTokenSource();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log);
            await screen.StartAsync(new("owner"), stop.Token);
            await Until(() => screen.IsActive("owner"));
            await control.StartAsync(new("owner"), stop.Token);
            await Until(() => states.Any(x => x.Status == "active"));
            control.Apply(new("owner", Key("keyDown", "AltLeft")));
            stop.Cancel();
            await Until(() => sink.Held == 0 && states.Any(x => x.Status == "stopped"));
        });

        await test("block inputs requires active control and is released when control stops", async () =>
        {
            var sink = new Sink(); var states = new ConcurrentQueue<ControlState>(); var blocker = new Blocker();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log, blocker: blocker);
            Assert(!control.SetBlocked(new("owner", true)).Blocked, "Blocked input before control was active");
            await Start(screen, control, states);
            Assert(!control.SetBlocked(new("other", true)).Blocked, "A different request blocked input");
            Assert(control.SetBlocked(new("owner", true)).Blocked && blocker.Blocked == 1, "Active control did not block local input");
            Assert(!control.SetBlocked(new("owner", false)).Blocked && blocker.Blocked == 0, "Unblock did not release local input");
            control.SetBlocked(new("owner", true));
            await control.StopAsync("owner");
            await Until(() => blocker.Blocked == 0);
            Assert(blocker.Blocked == 0, "Stopping control left local input blocked");
        });

        await test("malformed positions, buttons, wheel values and key codes never reach the input sink", async () =>
        {
            var sink = new Sink(); var states = new ConcurrentQueue<ControlState>();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log);
            await Start(screen, control, states);
            InputEvent[] bad = [new("move", double.NaN, 0, 0, 0, null),new("move", 1.1, 0, 0, 0, null),
                new("buttonDown", 0, 0, 9, 0, null),new("wheel", 0, 0, 0, int.MaxValue, null),
                Key("keyDown", "not-a-key"),new("arbitrary", 0, 0, 0, 0, null)];
            foreach (var input in bad) Assert(!control.Apply(new("owner", input)), "Malformed input reached the sink");
            Assert(sink.Applied == 0, "Invalid packets caused input");
        });

        await test("input device failure stops the control lease and releases held state", async () =>
        {
            var sink = new Sink(); var states = new ConcurrentQueue<ControlState>();
            await using var screen = Screen(log);
            await using var control = Control(screen, sink, states, log);
            await Start(screen, control, states);
            control.Apply(new("owner", Key("keyDown", "KeyA")));
            sink.Fail = true;
            Assert(!control.Apply(new("owner", Key("keyDown", "KeyB"))), "Failed injection was reported as successful");
            await Until(() => states.Any(x => x.Status == "stopped"));
            Assert(sink.Held == 0, "Input state was retained after an injector failure");
        });

        await test("absolute mouse positions cover primary displays with negative virtual desktop origins", () =>
        {
            Assert(RemoteInputRules.AbsolutePosition(0,0,0,0,1920,1080,0,0,1920,1080) == (0,0), "Incorrect top-left mapping");
            Assert(RemoteInputRules.AbsolutePosition(1,1,0,0,1920,1080,0,0,1920,1080) == (65535,65535), "Incorrect bottom-right mapping");
            var left = RemoteInputRules.AbsolutePosition(0,0,0,0,1920,1080,-1920,0,3840,1080);
            Assert(left.X is >=32767 and <=32777 && left.Y==0, "Negative virtual origin shifted the target cursor");
            Assert(RemoteInputRules.TryGetScanCode("ControlRight",out var scan,out var extended)&&scan==0x1d&&extended, "Right modifier is not extended");
            Assert(RemoteInputRules.TryGetScanCode("KeyA",out scan,out extended)&&scan==0x1e&&!extended, "Physical letter mapping is incorrect");
            return Task.CompletedTask;
        });
    }

    private static InputEvent Key(string kind,string key) => new(kind,0,0,0,0,key);
    private static ScreenShareSession Screen(Action<string> log) => new(
        (id,sequence,_)=>Task.FromResult(new ScreenFrame(id,1,1,"synthetic",sequence,DateTime.UtcNow)),
        (_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,log);
    private static RemoteControlSession Control(ScreenShareSession screen,Sink sink,ConcurrentQueue<ControlState> states,Action<string> log,TimeSpan? timeout=null,ILocalInputBlocker? blocker=null) =>
        new(screen,sink,(state,_)=>{states.Enqueue(state);return Task.CompletedTask;},log,timeout,blocker);
    private static async Task Start(ScreenShareSession screen,RemoteControlSession control,ConcurrentQueue<ControlState> states)
    {
        await screen.StartAsync(new("owner"),CancellationToken.None);await Until(()=>screen.IsActive("owner"));
        await control.StartAsync(new("owner"),CancellationToken.None);await Until(()=>states.Any(x=>x.Status=="active"));
    }
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task Until(Func<bool> condition)
    {
        var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<3000){if(condition())return;await Task.Delay(10);}throw new Exception("Control condition timed out");
    }
    private sealed class Sink : IRemoteInputSink
    {
        public int Applied;public int Held;public bool Fail;
        public void Apply(InputEvent e){if(Fail)throw new IOException("Synthetic input failure");Interlocked.Increment(ref Applied);if(e.Kind is "keyDown" or "buttonDown")Interlocked.Increment(ref Held);}
        public void ReleaseAll()=>Interlocked.Exchange(ref Held,0);
    }
    private sealed class Blocker : ILocalInputBlocker
    {
        public int Blocked;
        public bool SetBlocked(bool blocked){Blocked=blocked?1:0;return true;}
    }
}
