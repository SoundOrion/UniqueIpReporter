using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Filters;
using Serilog.Sinks.MSSqlServer;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;

//Serilog.Sinks.MSSqlServer は自動でテーブルを作ることも可能ですが、
//スキーマを自分で定義したい場合は、次のようにします：

//CREATE TABLE[dbo].[Logs] (
//    [Id] INT IDENTITY(1,1) PRIMARY KEY,
//    [TimeStamp] DATETIME2 NOT NULL,
//    [Level] NVARCHAR(20) NOT NULL,
//    [Message] NVARCHAR(MAX) NULL,
//    [EventType] NVARCHAR(50) NULL,
//    [Ip] NVARCHAR(45) NULL,
//    [Properties] NVARCHAR(MAX) NULL
//);

//応用: “IpFirstSeen” だけを SQL に送る

//大量の全ログを SQL に入れたくない場合は、
//サブロガーとフィルタを併用します：

//.WriteTo.Logger(lc => lc
//    .Filter.ByIncludingOnly(Serilog.Filters.Matching.WithProperty("EventType", "IpFirstSeen"))
//    .WriteTo.MSSqlServer(
//        connectionString: "...",
//        sinkOptions: new MSSqlServerSinkOptions
//        {
//            TableName = "UniqueIpEvents",
//            AutoCreateSqlTable = true
//        }))


//➡ これで「IP初回観測イベント」だけが UniqueIpEvents テーブルに保存されます。
//アプリ全体のログとは分離できます。

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

//    すべてのログが dbo.Logs に書き込まれます。
//AutoCreateSqlTable=true なら自動生成（簡易スキーマ）も可。
//EventType と Ip は LogContext 経由で自動挿入。
    .WriteTo.MSSqlServer(
        connectionString: "Server=localhost;Database=MyLogsDb;User Id=sa;Password=your_password;",
        sinkOptions: new MSSqlServerSinkOptions
        {
            TableName = "Logs",
            AutoCreateSqlTable = true, // テーブルが無ければ自動生成
        },
        columnOptions: new ColumnOptions
        {
            AdditionalColumns = new Collection<SqlColumn>
            {
                new SqlColumn { ColumnName = "EventType", DataType = System.Data.SqlDbType.NVarChar, DataLength = 50 },
                new SqlColumn { ColumnName = "Ip", DataType = System.Data.SqlDbType.NVarChar, DataLength = 45 }
            }
        })
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
