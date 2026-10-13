using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Velo;

// The HTTP surface behind `start --port`. It listens on loopback only, accepts only loopback host
// names, and requires the token from start.token on every /api request.
internal static class HttpApi
{
    private const string TokenHeader = "X-Velo-Token";
    private const int ReadChunkBytes = 64 * 1024;
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; " +
        "img-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    private static readonly string[] AllowedHosts = ["127.0.0.1", "localhost"];

    public static async Task<WebApplication> StartAsync(
        Config config,
        WorkQueue queue,
        WorkIntake intake,
        WorkReview review,
        RefreshSignal refresh,
        int port)
    {
        var token = CreateToken();
        await File.WriteAllTextAsync(config.TokenFile, token);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
        var app = builder.Build();
        // The token only means something while this server runs, so it is removed on every stop.
        app.Lifetime.ApplicationStopped.Register(() => File.Delete(config.TokenFile));

        app.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Cache-Control"] = "no-store";

            if (!AllowedHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase))
            {
                await Error(403, "forbidden_host", "Requests must address 127.0.0.1 or localhost.")
                    .ExecuteAsync(context);
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api") && !HasToken(context.Request, token))
            {
                await Error(401, "unauthorized", $"Send the contents of start.token in the {TokenHeader} header.")
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        var handlers = new Handlers(config, queue, intake, review, refresh);
        app.MapGet("/", () => Dashboard.Page(token));
        app.MapGet("/dashboard.js", () => Dashboard.Asset("dashboard.js", "text/javascript; charset=utf-8"));
        app.MapGet("/dashboard.css", () => Dashboard.Asset("dashboard.css", "text/css; charset=utf-8"));
        app.MapGet("/api/v1/state", () => handlers.State());
        app.MapMethods("/api/v1/state", ["POST", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapGet("/api/v1/work", (HttpRequest request) => handlers.List(request));
        app.MapPost("/api/v1/work", (HttpRequest request) => handlers.Add(request));
        app.MapMethods("/api/v1/work", ["PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapPost("/api/v1/refresh", () => handlers.Refresh());
        app.MapMethods("/api/v1/refresh", ["GET", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapGet("/api/v1/{id}", (string id) => handlers.Detail(id));
        app.MapPatch("/api/v1/{id}", (string id, HttpRequest request) => handlers.Edit(id, request));
        app.MapDelete("/api/v1/{id}", (string id) => handlers.Delete(id));
        app.MapMethods("/api/v1/{id}", ["POST", "PUT"], () => MethodNotAllowed());
        app.MapGet("/api/v1/{id}/log", (string id, HttpRequest request) => handlers.Log(id, request));
        app.MapMethods("/api/v1/{id}/log", ["POST", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapPost("/api/v1/{id}/cancel", (string id) => handlers.Cancel(id));
        app.MapMethods("/api/v1/{id}/cancel", ["GET", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapPost("/api/v1/{id}/retry", (string id) => handlers.Retry(id));
        app.MapMethods("/api/v1/{id}/retry", ["GET", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapGet("/api/v1/{id}/diff", (string id) => handlers.Diff(id));
        app.MapMethods("/api/v1/{id}/diff", ["POST", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapPost("/api/v1/{id}/accept", (string id) => handlers.Accept(id));
        app.MapMethods("/api/v1/{id}/accept", ["GET", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapPost("/api/v1/{id}/discard", (string id) => handlers.Discard(id));
        app.MapMethods("/api/v1/{id}/discard", ["GET", "PUT", "PATCH", "DELETE"], () => MethodNotAllowed());
        app.MapFallback(context => Error(404, "not_found", "No such endpoint.").ExecuteAsync(context));

        try
        {
            await app.StartAsync();
        }
        catch
        {
            // The stop callback never runs for an app that did not start, so remove the token here.
            await app.DisposeAsync();
            File.Delete(config.TokenFile);
            throw;
        }
        Console.WriteLine($"HTTP API listening on {string.Join(", ", app.Urls)}.");
        return app;
    }

    private static IResult MethodNotAllowed() =>
        Error(405, "method_not_allowed", "Method not allowed for this route.");

    private static IResult Error(int status, string code, string message) =>
        Json(new ApiErrorEnvelope(new ApiError(code, message)), ApiJsonContext.Default.ApiErrorEnvelope, status);

    private static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo, int status = StatusCodes.Status200OK) =>
        Results.Content(JsonSerializer.Serialize(value, typeInfo), "application/json", Encoding.UTF8, status);

    private static bool HasToken(HttpRequest request, string token) =>
        request.Headers.TryGetValue(TokenHeader, out var given)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given.ToString()),
            Encoding.UTF8.GetBytes(token));

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class Handlers(Config config, WorkQueue queue, WorkIntake intake, WorkReview review, RefreshSignal refresh)
    {
        public IResult State()
        {
            var work = queue.List();
            return Json(new StateResponse(
                    DateTimeOffset.UtcNow,
                    new WorkCounts(
                        Count(work, WorkState.Pending),
                        Count(work, WorkState.Running),
                        Count(work, WorkState.Succeeded),
                        Count(work, WorkState.Failed)),
                    Summaries(work, WorkState.Running),
                    Summaries(work, WorkState.Pending)),
                ApiJsonContext.Default.StateResponse);
        }

        public IResult List(HttpRequest request)
        {
            var filter = request.Query["state"].ToString();
            WorkState? wanted = null;
            if (filter.Length > 0)
            {
                if (!WorkStates.TryParse(filter, out var state))
                    return Error(400, "invalid_request", "state must be pending, running, succeeded, or failed.");
                wanted = state;
            }

            var work = queue.List().Where(item => wanted is null || item.State == wanted);
            return Json(
                new WorkListResponse(work.Select(Summary).ToArray()),
                ApiJsonContext.Default.WorkListResponse);
        }

        public async Task<IResult> Add(HttpRequest request)
        {
            AddWorkRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync(ApiJsonContext.Default.AddWorkRequest);
            }
            catch (Exception error) when (error is JsonException or BadHttpRequestException)
            {
                return Error(400, "invalid_request", $"Request body is not valid JSON: {error.Message}");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
                return Error(400, "invalid_request", "prompt is required.");
            if (body.Workspace is null || !Path.IsPathFullyQualified(body.Workspace))
                return Error(400, "invalid_request", "workspace must be an absolute path.");

            try
            {
                var id = await intake.AddAsync(body.Prompt, body.Workspace, body.Worktree);
                return Json(
                    new WorkStatusResponse(id, WorkState.Pending.Text()),
                    ApiJsonContext.Default.WorkStatusResponse,
                    StatusCodes.Status201Created);
            }
            catch (Exception error) when (error is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
            {
                return Error(400, "invalid_request", error.Message);
            }
            catch (Win32Exception error)
            {
                // git could not be started at all, usually because it is not on PATH.
                return Error(500, "git_failed", error.Message);
            }
        }

        public IResult Refresh()
        {
            var coalesced = !refresh.Request();
            return Json(
                new RefreshResponse(true, coalesced, DateTimeOffset.UtcNow, ["poll"]),
                ApiJsonContext.Default.RefreshResponse,
                StatusCodes.Status202Accepted);
        }

        public IResult Detail(string id)
        {
            var item = Find(id);
            return item is null
                ? NotFound(id)
                : Json(ToDetail(item), ApiJsonContext.Default.WorkDetail);
        }

        public async Task<IResult> Edit(string id, HttpRequest request)
        {
            if (!WorkQueue.IsValidId(id)) return NotFound(id);

            EditWorkRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync(ApiJsonContext.Default.EditWorkRequest);
            }
            catch (Exception error) when (error is JsonException or BadHttpRequestException)
            {
                return Error(400, "invalid_request", $"Request body is not valid JSON: {error.Message}");
            }
            if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
                return Error(400, "invalid_request", "prompt is required.");

            return queue.Edit(id, body.Prompt) switch
            {
                WorkChange.Done => Detail(id),
                WorkChange.NotPending => Error(409, "work_not_pending", "Only pending work can be edited."),
                _ => NotFound(id)
            };
        }

        public IResult Delete(string id)
        {
            if (!WorkQueue.IsValidId(id)) return NotFound(id);

            return queue.Delete(id) switch
            {
                WorkChange.Done => Results.NoContent(),
                WorkChange.Running => Error(409, "work_running", "Running work cannot be deleted; cancel it first."),
                _ => NotFound(id)
            };
        }

        public IResult Log(string id, HttpRequest request)
        {
            if (Find(id) is null) return NotFound(id);

            var offsetText = request.Query["offset"].ToString();
            long requested = 0;
            if (offsetText.Length > 0 && (!long.TryParse(offsetText, out requested) || requested < 0))
                return Error(400, "invalid_request", "offset must be a non-negative integer.");

            var path = config.LogFile(id);
            if (!File.Exists(path))
                return Json(new LogResponse(id, 0, 0, string.Empty), ApiJsonContext.Default.LogResponse);

            // Reads at most one chunk, so a poll never returns a whole large log.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Min(requested, stream.Length);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[ReadChunkBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var length = read;
            if (start + read < stream.Length && read > 0)
            {
                // When the chunk holds a newline, end on it so a poll does not split a line.
                var newline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
                if (newline >= 0) length = newline + 1;
            }

            return Json(
                new LogResponse(id, start, start + length, Encoding.UTF8.GetString(buffer, 0, length)),
                ApiJsonContext.Default.LogResponse);
        }

        public IResult Cancel(string id)
        {
            if (!WorkQueue.IsValidId(id)) return NotFound(id);

            return queue.Cancel(id) switch
            {
                CancelResult.Cancelled => Json(new CancelResponse(id, "cancelled"), ApiJsonContext.Default.CancelResponse),
                CancelResult.Requested => Json(new CancelResponse(id, "cancel_requested"), ApiJsonContext.Default.CancelResponse),
                CancelResult.NotCancellable => Error(409, "work_not_cancellable", "Only pending or running work can be cancelled."),
                _ => NotFound(id)
            };
        }

        public IResult Retry(string id)
        {
            if (!WorkQueue.IsValidId(id)) return NotFound(id);
            if (queue.TryRetry(id))
            {
                return Json(
                    new WorkStatusResponse(id, WorkState.Pending.Text()),
                    ApiJsonContext.Default.WorkStatusResponse);
            }
            return Find(id) is null
                ? NotFound(id)
                : Error(409, "work_not_failed", "Only failed work can be retried.");
        }

        public async Task<IResult> Diff(string id)
        {
            if (!WorkQueue.IsValidId(id)) return NotFound(id);

            var outcome = await review.DiffAsync(id);
            return outcome.Result == ReviewResult.Done
                ? Json(
                    new DiffResponse(id, outcome.Diff!.Base, outcome.Diff.Stat, outcome.Diff.Patch, outcome.Diff.Truncated),
                    ApiJsonContext.Default.DiffResponse)
                : ReviewError(id, outcome);
        }

        public async Task<IResult> Accept(string id)
        {
            if (!WorkQueue.IsValidId(id)) return NotFound(id);

            var outcome = await review.AcceptAsync(id);
            return outcome.Result == ReviewResult.Done
                ? Json(new AcceptResponse(id, true), ApiJsonContext.Default.AcceptResponse)
                : ReviewError(id, outcome);
        }

        public async Task<IResult> Discard(string id)
        {
            if (!WorkQueue.IsValidId(id)) return NotFound(id);

            var outcome = await review.DiscardAsync(id);
            return outcome.Result == ReviewResult.Done
                ? Json(new DiscardResponse(id, true), ApiJsonContext.Default.DiscardResponse)
                : ReviewError(id, outcome);
        }

        private static IResult ReviewError(string id, ReviewOutcome outcome) => outcome.Result switch
        {
            ReviewResult.NotFound => NotFound(id),
            ReviewResult.NotReviewable => Error(409, "work_not_reviewable", outcome.Message),
            ReviewResult.WorktreeMissing => Error(409, "worktree_missing", outcome.Message),
            ReviewResult.AlreadyAccepted => Error(409, "work_already_accepted", outcome.Message),
            ReviewResult.Conflict => Error(409, "accept_conflict", outcome.Message),
            _ => Error(500, "git_failed", outcome.Message)
        };

        private WorkItem? Find(string id) => WorkQueue.IsValidId(id) ? queue.Get(id) : null;

        private static IResult NotFound(string id) =>
            Error(404, "work_not_found", $"Work not found: {id}");

        private static int Count(IEnumerable<WorkItem> work, WorkState state) =>
            work.Count(item => item.State == state);

        private static WorkSummary[] Summaries(IEnumerable<WorkItem> work, WorkState state) =>
            work.Where(item => item.State == state).Select(Summary).ToArray();

        private static WorkSummary Summary(WorkItem item) => new(
            item.Id,
            item.Title,
            item.State.Text(),
            new WorkspaceInfo(item.Workspace),
            item.Attempts,
            item.CreatedAt,
            item.StartedAt,
            item.CancelRequested,
            item.Error,
            item.CanReview(),
            item.Discarded,
            item.Accepted);

        private WorkDetail ToDetail(WorkItem item)
        {
            var log = config.LogFile(item.Id);
            return new WorkDetail(
                item.Id,
                item.State.Text(),
                item.Prompt,
                new WorkspaceInfo(item.Workspace),
                item.Attempts,
                item.CreatedAt,
                item.StartedAt,
                item.FinishedAt,
                item.ExitCode,
                item.CancelRequested,
                item.Error,
                new LogInfo(log, File.Exists(log)),
                item.CanReview(),
                item.Discarded,
                item.Accepted);
        }
    }
}
