using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Filters;
using System.Net;
using System.Net.Sockets;

var builder = Host.CreateApplicationBuilder(args);

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

//// （任意）最小Web API（現在のIP一覧と統計を見れる）
//builder.Services.AddHostedService<WebApiService>();

var host = builder.Build();
await host.RunAsync();

/// <summary>受信ポートやTTLなどの設定</summary>
public class UniqueIpOptions
{
    public int Port { get; set; } = 5000;
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(30);

    // 定期掃除の間隔
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
