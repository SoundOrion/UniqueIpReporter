using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text;

public class TcpListenerService : BackgroundService
{
    private readonly UniqueIpStore _store;
    private readonly UniqueIpOptions _opt;
    private TcpListener? _listener;

    public TcpListenerService(UniqueIpStore store, IOptions<UniqueIpOptions> options)
    {
        _store = store;
        _opt = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Any, _opt.Port);
        _listener.Start();
        Console.WriteLine($"[TcpListener] Listening on 0.0.0.0:{_opt.Port} ...");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            try { _listener.Stop(); } catch { /* ignore */ }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            _store.TryAddOrTouch(ip);

            using var stream = client.GetStream();
            var buf = new byte[8192];

            while (!token.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, 0, buf.Length, token); }
                catch { break; }
                if (n == 0) break;

                // UTF-8テキストとして表示（用途に合わせて処理変更OK）
                var text = Encoding.UTF8.GetString(buf, 0, n);
                Console.WriteLine($"[{ip}] {text}");
                _store.TryAddOrTouch(ip); // 受信のたびに「最終観測時刻」を更新
            }
        }
    }
}
