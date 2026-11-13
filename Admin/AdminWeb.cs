using System.Text.Json;
using RemoteAdmin.Shared;

public static class AdminWeb
{
    public static void UseAuthentication(WebApplication app, RemoteAdmin.AdminAuth auth)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // Clients keep their own WebSocket route; the login endpoint must be
            // reachable without a session so a browser can obtain one.
            if (path.StartsWithSegments("/client") || path == "/api/login")
            {
                await next();
                return;
            }

            if (auth.IsSignedIn(context))
            {
                await next();
                return;
            }

            if (path == "/" || path == "/login")
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsync(ReadAsset("login.html"));
                return;
            }

            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/ws"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Sign in to the Admin dashboard first." });
                return;
            }

            context.Response.Redirect("/login");
        });
    }

    public static void MapAuthentication(WebApplication app, RemoteAdmin.AdminAuth auth, DeviceHub hub)
    {
        app.MapPost("/api/login", async (HttpContext context) =>
        {
            LoginRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<LoginRequest>(context.Request.Body, Protocol.Json);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid login request." });
            }

            var token = auth.SignIn(request?.Username, request?.Password);
            if (token is null)
            {
                // Slow down credential guessing without making the UI feel broken.
                await Task.Delay(400);
                return Results.Json(new { error = "Incorrect username or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            context.Response.Cookies.Append(RemoteAdmin.AdminAuth.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false, // The dashboard is served over http://127.0.0.1.
                Path = "/",
                IsEssential = true
            });
            return Results.Ok(new { authenticated = true, username = RemoteAdmin.AdminAuth.Username });
        });

        app.MapPost("/api/logout", (HttpContext context) =>
        {
            auth.SignOut(context.Request.Cookies[RemoteAdmin.AdminAuth.CookieName]);
            context.Response.Cookies.Delete(RemoteAdmin.AdminAuth.CookieName);
            // The last browser leaving signs the Admin out, so updated clients
            // must disappear until somebody signs in again. Legacy clients stay.
            if (!auth.IsAnySignedIn) hub.DisconnectSignedInClients();
            return Results.Ok(new { authenticated = false });
        });
    }

    public static void MapAssets(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(ReadAsset("index.html"), "text/html"));
        app.MapGet("/login", () => Results.Redirect("/"));
        app.MapGet("/screen.js", () => Results.Content(ReadAsset("screen.js"), "text/javascript"));
    }

    public static string ReadAsset(string fileName)
    {
        var resource = typeof(AdminWeb).Assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("wwwroot." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resource is null) return "<h1>Dashboard resource missing</h1>";
        using var stream = typeof(AdminWeb).Assembly.GetManifestResourceStream(resource);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private sealed record LoginRequest(string? Username, string? Password);
}
