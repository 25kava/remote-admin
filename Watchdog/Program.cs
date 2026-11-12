using RemoteAdmin.Guard;

// c2 is deliberately tiny: it owns no admin functionality and only keeps c1.exe alive.
using var instance = SingleInstance.Acquire(GuardPaths.C2MutexName);
if (!instance.Owned) return;

using var stopping = new CancellationTokenSource();
await using var guard = new GuardSupervisor(GuardRole.C2, log: null, stopping);
guard.Start();

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token);
}
catch (OperationCanceledException)
{
}
