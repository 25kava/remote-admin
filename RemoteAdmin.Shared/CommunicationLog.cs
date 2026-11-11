namespace RemoteAdmin.Shared;

// A diagnostic failure must never tear down a network session.
public sealed class CommunicationLog(string path)
{
    private readonly object gate = new();

    public void Write(string message)
    {
        lock (gate)
        {
            try { File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
