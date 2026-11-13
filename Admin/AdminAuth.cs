using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RemoteAdmin;

/// <summary>
/// Prototype dashboard authentication. Credentials are hardcoded for now and
/// sessions live in memory so restarting Admin.exe signs every browser out.
/// </summary>
public sealed class AdminAuth(Action<string> log)
{
    public const string Username = "admin";
    public const string Password = "1234";
    public const string CookieName = "remoteadmin_session";

    // A signed-in browser stays valid while it keeps using the dashboard. When
    // the last session expires the Admin reports itself as signed out again so
    // updated clients stop publishing themselves.
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTime> sessions = new();

    public bool IsAnySignedIn
    {
        get
        {
            PruneExpired();
            return !sessions.IsEmpty;
        }
    }

    public bool IsSignedIn(HttpContext context) => IsSignedIn(context.Request.Cookies[CookieName]);

    public bool IsSignedIn(string? token)
    {
        if (string.IsNullOrEmpty(token) || !sessions.TryGetValue(token, out var expires)) return false;
        if (expires <= DateTime.UtcNow)
        {
            sessions.TryRemove(token, out _);
            return false;
        }
        // Sliding expiration: only an idle dashboard session times out.
        sessions[token] = DateTime.UtcNow + SessionLifetime;
        return true;
    }

    public string? SignIn(string? username, string? password)
    {
        if (!Matches(username, Username) || !Matches(password, Password))
        {
            log($"Rejected Admin sign-in for user '{Truncate(username)}'.");
            return null;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        sessions[token] = DateTime.UtcNow + SessionLifetime;
        log("Admin dashboard signed in.");
        return token;
    }

    public void SignOut(string? token)
    {
        if (string.IsNullOrEmpty(token) || !sessions.TryRemove(token, out _)) return;
        log("Admin dashboard signed out.");
    }

    /// <summary>Drops idle sessions and reports whether the last one expired.</summary>
    public bool PruneExpired()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var session in sessions)
        {
            if (session.Value > now) continue;
            if (sessions.TryRemove(session.Key, out _)) removed = true;
        }
        return removed && sessions.IsEmpty;
    }

    private static bool Matches(string? supplied, string expected)
    {
        if (supplied is null) return false;
        var provided = Encoding.UTF8.GetBytes(supplied);
        var wanted = Encoding.UTF8.GetBytes(expected);
        return provided.Length == wanted.Length && CryptographicOperations.FixedTimeEquals(provided, wanted);
    }

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Length > 64 ? value[..64] : value;
}
