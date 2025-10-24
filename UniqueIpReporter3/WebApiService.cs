//using System.Text.Json;

//public class WebApiService : BackgroundService
//{
//    private readonly IHostApplicationLifetime _lifetime;
//    private readonly UniqueIpStore _store;
//    private WebApplication? _app;

//    public WebApiService(IHostApplicationLifetime lifetime, UniqueIpStore store)
//    {
//        _lifetime = lifetime;
//        _store = store;
//    }

//    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//    {
//        var builder = WebApplication.CreateBuilder();
//        _app = builder.Build();

//        _app.MapGet("/unique-ips", () =>
//        {
//            var snapshot = _store.Snapshot()
//                                 .OrderBy(kv => kv.Key.ToString())
//                                 .Select(kv => new { ip = kv.Key.ToString(), lastSeenUtc = kv.Value });
//            return Results.Json(snapshot, new JsonSerializerOptions { WriteIndented = true });
//        });

//        _app.MapGet("/stats", () =>
//        {
//            return Results.Json(new { count = _store.Count }, new JsonSerializerOptions { WriteIndented = true });
//        });

//        // アプリ終了に合わせて停止
//        _lifetime.ApplicationStopping.Register(() => _app?.StopAsync().GetAwaiter().GetResult());

//        await _app.StartAsync(stoppingToken);
//        Console.WriteLine("[WebApi] GET /unique-ips  /stats");
//        await _app.WaitForShutdownAsync(stoppingToken);
//    }
//}
