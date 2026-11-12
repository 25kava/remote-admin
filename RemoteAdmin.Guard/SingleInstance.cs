namespace RemoteAdmin.Guard;

/// <summary>
/// Holds a named mutex for the lifetime of a process. The sibling watchdog checks the same
/// mutex to decide whether its partner is alive; no process enumeration or privileges are needed.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private Mutex? mutex;
    private bool owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        this.mutex = mutex;
        this.owned = owned;
    }

    public bool Owned => owned;

    public static SingleInstance Acquire(string name)
    {
        var mutex = new Mutex(false, name, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    public void Dispose()
    {
        if (mutex is null) return;
        if (owned)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        mutex.Dispose();
        mutex = null;
        owned = false;
    }
}
