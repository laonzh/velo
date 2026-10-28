using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Velo;

internal static class Api
{
    public static void Map(WebApplication app, Engine engine)
    {
        JsonHttpResult<Issue> Ok(Issue issue, int status = 200) => TypedResults.Json(engine.WithTakeOver(issue), VeloJson.Default.Issue, statusCode: status);

        // The body is optional for /run, so read it as text: no content-type requirements, empty = null.
        static async Task<T?> Body<T>(HttpRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        {
            string text = await new StreamReader(request.Body).ReadToEndAsync(request.HttpContext.RequestAborted);
            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize(text, type);
        }

        // Query values are bound as strings and parsed here, so a bad value still gets an {"error": ...} body.
        static long? Number(string? s) => string.IsNullOrEmpty(s) ? null
            : long.TryParse(s, System.Globalization.NumberStyles.None, null, out long n) ? n : throw VeloException.BadRequest($"'{s}' is not a number");

        // Polled by the web UI every 2 s: no-cache makes the browser revalidate, and the store version answers 304 while nothing
        // changed. The version is read before the list, so an ETag never stands for newer issues than the body it comes with.
        app.MapGet("/api/issues", IResult (string? all, HttpContext ctx) =>
        {
            string etag = $"\"{engine.Version}\"";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch == etag) return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            return TypedResults.Json<IReadOnlyList<Issue>>([.. engine.List(string.Equals(all, "true", StringComparison.OrdinalIgnoreCase)).Select(engine.WithTakeOver)], VeloJson.Default.IReadOnlyListIssue);
        });
        app.MapPost("/api/issues", async (HttpRequest request) =>
            Ok(await engine.AddAsync(await Body(request, VeloJson.Default.NewIssue) ?? throw VeloException.BadRequest("request body is required")), 201));
        app.MapGet("/api/models", async () => TypedResults.Json(await engine.ModelsAsync(), VeloJson.Default.IReadOnlyListAgentModel));
        app.MapGet("/api/issues/{id}", (string id) => Ok(engine.Get(id)));
        app.MapDelete("/api/issues/{id}", async (string id) =>
        {
            await engine.DeleteAsync(id);
            return TypedResults.NoContent();
        });
        app.MapPost("/api/issues/{id}/run", async (string id, HttpRequest request) =>
        {
            RunRequest? run = await Body(request, VeloJson.Default.RunRequest);
            return Ok(engine.RunIssue(id, run?.Message, run?.Model, run?.Effort));
        });
        app.MapPost("/api/issues/{id}/stop", async (string id) => Ok(await engine.StopAsync(id)));
        app.MapPost("/api/issues/{id}/approve", async (string id) => Ok(await engine.ApproveAsync(id)));
        app.MapGet("/api/issues/{id}/diff", async (string id) => TypedResults.Text(await engine.DiffAsync(id), "text/plain; charset=utf-8"));
        app.MapGet("/api/issues/{id}/merge", async (string id) => TypedResults.Json(await engine.MergeCheckAsync(id), VeloJson.Default.MergeCheck));
        app.MapGet("/api/issues/{id}/logs", (string id, string? run, string? offset) =>
            TypedResults.Json(engine.ReadLog(id, (int?)Number(run), Number(offset) ?? 0), VeloJson.Default.LogChunk));
        app.Map("/api/{**rest}", ctx => Guard.Error(ctx, 404, "not found"));
    }
}
