using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;

public class TcpListenerService : BackgroundService
{
    private readonly ConcurrentDictionary<IPAddress, byte> _uniqueIps;
    private TcpListener? _listener;

    public TcpListenerService(ConcurrentDictionary<IPAddress, byte> uniqueIps)
    {
        _uniqueIps = uniqueIps;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Any, 5000);
        _listener.Start();
        Console.WriteLine("Listening on 0.0.0.0:5000 ...");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            _uniqueIps.TryAdd(ip, 0);

            using var stream = client.GetStream();
            var buf = new byte[8192];

            while (!token.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, 0, buf.Length, token); }
                catch { break; }
                if (n == 0) break;

                var text = Encoding.UTF8.GetString(buf, 0, n);
                Console.WriteLine($"[{ip}] {text}");
            }
        }
    }
}
