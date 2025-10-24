using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

// ConcurrentDictionary<IPAddress, byte> をシングルトン登録
builder.Services.AddSingleton(new ConcurrentDictionary<IPAddress, byte>());

// TCPリスナーを実行するバックグラウンドサービス
builder.Services.AddHostedService<TcpListenerService>();

// uniqueIps を使う別のバックグラウンドサービス
builder.Services.AddHostedService<UniqueIpReporterService>();

var host = builder.Build();
await host.RunAsync();
