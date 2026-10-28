using System.Text.Json;

namespace Velo;

internal static class Guard
{
    /// <summary>
    /// Host check (400), X-Velo header on non-GET /api requests (403), and exceptions → <c>{"error": ...}</c>.
    /// </summary>
    public static async Task Invoke(HttpContext ctx, RequestDelegate next)
    {
        HttpRequest request = ctx.Request;
        string host = request.Host.Host;
        if (host != "127.0.0.1" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            await Error(ctx, 400, "invalid Host header");
            return;
        }
        if (request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(request.Method) && request.Headers["X-Velo"] != "1")
        {
            await Error(ctx, 403, "missing header X-Velo: 1");
            return;
        }
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
        {
            (int status, string message) = ex switch
            {
                VeloException v => (v.Status, v.Message),
                GitException => (409, ex.Message),
                JsonException or BadHttpRequestException => (400, ex.Message),
                _ => (500, "internal error: " + ex.Message),
            };
            if (status == 500) Console.Error.WriteLine($"velo: {request.Method} {request.Path} failed: {ex}");
            await Error(ctx, status, message);
        }
    }

    public static Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ErrorBody(message), VeloJson.Default.ErrorBody);
    }
}
