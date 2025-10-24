using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Filters;
using System.Net;
using System.Net.Sockets;

var builder = Host.CreateApplicationBuilder(args);

// �� Serilog �\���i�ŏ��Ɂj--------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    // �ӂ��̃A�v���S�̃��O
    .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    // �g����ϑ�IP�h������ʃt�@�C����
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(Matching.WithProperty("EventType", "IpFirstSeen"))
        .WriteTo.File("logs/unique-ips-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30))
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger);

// �ݒ�i�|�[�g�ETTL�j
builder.Services.Configure<UniqueIpOptions>(opt =>
{
    opt.Port = 5000;
    opt.EntryTtl = TimeSpan.FromMinutes(30); // 30���A�N�Z�X�Ȃ���Α|��
    opt.CleanupInterval = TimeSpan.FromMinutes(5); // 5�����Ƃɑ|��
});

// �X�g�A�i�V���O���g���j
builder.Services.AddSingleton<UniqueIpStore>();

// �o�b�N�O���E���h�T�[�r�X
builder.Services.AddHostedService<TcpListenerService>();
builder.Services.AddHostedService<UniqueIpCleanupService>();
builder.Services.AddHostedService<UniqueIpReporterService>(); // �C�Ӂi�󋵃��O�j

//// �i�C�Ӂj�ŏ�Web API�i���݂�IP�ꗗ�Ɠ��v�������j
//builder.Services.AddHostedService<WebApiService>();

var host = builder.Build();
await host.RunAsync();

/// <summary>��M�|�[�g��TTL�Ȃǂ̐ݒ�</summary>
public class UniqueIpOptions
{
    public int Port { get; set; } = 5000;
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(30);

    // ����|���̊Ԋu
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
