using System.Threading.Channels;
using FcadServe;
using FcadServe.Api;
using FcadServe.FreeCad;
using FcadServe.Health;
using FcadServe.Jobs;
using FcadServe.Options;
using FcadServe.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("fcadserve.json", optional: true)
    .AddEnvironmentVariables("FCADSERVE_");

// WebApplication's configuration root resolves exact-case keys for root-level
// scalars, but the FCADSERVE_* environment variables (and test-injected
// settings) are upper-cased (STATE_ROOT, FREECAD__MODE). Re-add the env-scoped
// keys normalized to the PascalCase paths option binding expects (`StateRoot`,
// `FreeCad:Mode`) as an in-memory provider. The provider is inserted before the
// command line so genuine CLI arguments (`--Jobs:TimeoutSeconds=...`) retain
// precedence over the environment.
var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
foreach (var kv in builder.Configuration.AsEnumerable())
{
    var key = kv.Key;
    if (!key.StartsWith("FCADSERVE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("FCADSERVE_fcadserve", StringComparison.OrdinalIgnoreCase))
        continue;

    var name = ConfigKey.Normalize(key["FCADSERVE_".Length..]);
    var value = kv.Value;
    if (value is null)
        continue;

    // The option binder binds string[] properties from indexed children
    // ("AllowedInputRoots:0"), so split the raw ;/,-separated value.
    if (name is "AllowedInputRoots" or "AllowedOutputRoots")
    {
        var items = value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0)
            continue;
        for (var i = 0; i < items.Length; i++)
            normalized[$"{name}:{i}"] = items[i];
        continue;
    }

    normalized[name] = value;
}
builder.Configuration.AddInMemoryCollection(normalized);
builder.Configuration.AddCommandLine(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new SubmitJobRequestConverter()));

var options = builder.Configuration.Get<ServiceOptions>() ?? new ServiceOptions();
options.AllowedInputRoots = PathUtils.SplitList(options.AllowedInputRoots);
options.AllowedOutputRoots = PathUtils.SplitList(options.AllowedOutputRoots);

builder.Logging.SetMinimumLevel(ParseLevel(options.LogLevel));
builder.WebHost.UseUrls($"http://{options.BindAddress}:{options.Port}");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new PathPolicy(options));
builder.Services.AddSingleton<JobDatabase>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<ActiveProcessRegistry>();
builder.Services.AddSingleton<IFreeCadRunner, FreeCadRunner>();
builder.Services.AddSingleton<JobExecutor>();
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<ServiceOptions>();
    return Channel.CreateBounded<JobRecord>(new BoundedChannelOptions(Math.Max(1, opts.Jobs.QueueCapacity))
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
    });
});

builder.Services.AddSingleton<JobDirector>();
builder.Services.AddSingleton<JobQueueWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobDirector>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueueWorker>());

var app = builder.Build();
app.MapEndpoints();

app.Run();

static LogLevel ParseLevel(string level) =>
    Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogLevel.Information;

public partial class Program;