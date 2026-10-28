using System.Net;

namespace Velo;

/// <summary><c>velo serve</c>: Kestrel on 127.0.0.1, the /api endpoints and the embedded web UI.</summary>
public static class Server
{
    public static async Task<int> RunAsync(VeloPaths paths)
    {
        FileStream instanceLock;
        try
        {
            Directory.CreateDirectory(paths.Root);
            instanceLock = new FileStream(Path.Combine(paths.Root, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"velo: cannot lock data directory {paths.Root} (another server may be running): {ex.Message}");
            return 1;
        }
        using (instanceLock) return await RunLockedAsync(paths);
    }

    private static async Task<int> RunLockedAsync(VeloPaths paths)
    {
        Settings settings;
        Store store;
        try
        {
            settings = Store.LoadSettings(paths);
            store = new Store(paths);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("velo: " + ex.Message);
            return 1;
        }
        var engine = new Engine(paths, store, settings);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.None); // a taken port is reported below, without a stack trace
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Listen(IPAddress.Loopback, settings.Port);
        });
        await using WebApplication app = builder.Build();
        app.Use(Guard.Invoke);
        Api.Map(app, engine);
        StaticWeb.Map(app);

        try
        {
            await app.StartAsync();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"velo: cannot listen on 127.0.0.1:{settings.Port} (is another velo serve running?): {ex.Message}");
            return 1;
        }
        engine.Start();
        Console.WriteLine($"velo {Cli.Version} listening on http://127.0.0.1:{settings.Port}/ (data: {paths.Root})");
        await app.WaitForShutdownAsync();
        await engine.ShutdownAsync();
        return 0;
    }
}
