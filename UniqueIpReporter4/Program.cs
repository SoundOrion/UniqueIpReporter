using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Filters;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);

// ★ Serilog 構成（最初に）--------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    // ふつうのアプリ全体ログ
    .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    // “初回観測IP”だけを別ファイルへ
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(Matching.WithProperty("EventType", "IpFirstSeen"))
        .WriteTo.File("logs/unique-ips-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30))
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger);

// 設定（ポート・TTL）
builder.Services.Configure<UniqueIpOptions>(opt =>
{
    opt.Port = 5000;
    opt.EntryTtl = TimeSpan.FromMinutes(30); // 30分アクセスなければ掃除
    opt.CleanupInterval = TimeSpan.FromMinutes(5); // 5分ごとに掃除
});

// ストア（シングルトン）
builder.Services.AddSingleton<UniqueIpStore>();

// バックグラウンドサービス
builder.Services.AddHostedService<TcpListenerService>();
builder.Services.AddHostedService<UniqueIpCleanupService>();
builder.Services.AddHostedService<UniqueIpReporterService>(); // 任意（状況ログ）

var app = builder.Build();

// ---- Kestrel の待受ポートを Options から設定 ----
var opt = app.Services.GetRequiredService<IOptions<UniqueIpOptions>>().Value;
// 既定URLを上書き（docker 等で: "http://0.0.0.0" を推奨）
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{opt.ApiPort}");

// ---- Minimal API エンドポイント ----
// 一覧（IP と最終観測UTC）
app.MapGet("/unique-ips", (UniqueIpStore store) =>
{
    var snapshot = store.Snapshot()
                        .OrderBy(kv => kv.Key.ToString())
                        .Select(kv => new { ip = kv.Key.ToString(), lastSeenUtc = kv.Value });
    return Results.Ok(snapshot);
});

// 件数だけ
app.MapGet("/stats", (UniqueIpStore store) =>
{
    return Results.Ok(new { count = store.Count });
});

// 起動ログ（確認用）
app.Logger.LogInformation("Web API listening on http://0.0.0.0:{port} (TCP listener on {tcp})",
    opt.ApiPort, opt.Port);

// 実行
await app.RunAsync();

/// <summary>受信ポートやTTLなどの設定</summary>
public class UniqueIpOptions
{
    public int Port { get; set; } = 5000;
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(30);

    // 定期掃除の間隔
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
