namespace Velo;

internal static class StaticWeb
{
    /// <summary>
    /// The web UI embedded as resources <c>web/&lt;file&gt;</c>: <c>/</c> → index.html, other files by name, else 404.
    /// </summary>
    public static void Map(WebApplication app)
    {
        var assembly = typeof(StaticWeb).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)))
        {
            using var ms = new MemoryStream();
            using (Stream s = assembly.GetManifestResourceStream(name)!) s.CopyTo(ms);
            files[name["web/".Length..].Replace('\\', '/')] = ms.ToArray(); // %(RecursiveDir) uses '\' on Windows
        }

        app.MapGet("/{**path}", (string? path) =>
        {
            path = string.IsNullOrEmpty(path) ? "index.html" : path;
            if (!files.TryGetValue(path, out byte[]? content)) return Results.NotFound();
            string type = Path.GetExtension(path) switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream",
            };
            return Results.Bytes(content, type);
        });
    }
}
