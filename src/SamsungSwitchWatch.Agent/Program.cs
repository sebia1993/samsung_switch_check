using System.Text.Json.Serialization;
using SamsungSwitchWatch.Agent.Api;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent;

public static class AgentApplication
{
    public static WebApplication Build(
        string[] args,
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => options.ServiceName = "SamsungSwitchWatchAgent");
        if (overrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        var options = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
        AgentOptionsValidator.ValidateAndNormalize(options, builder.Environment.ContentRootPath);

        if (options.Https.Enabled)
        {
            var certificatePath = Path.IsPathRooted(options.Https.CertificatePath)
                ? options.Https.CertificatePath
                : Path.Combine(builder.Environment.ContentRootPath, options.Https.CertificatePath);
            var certificatePassword = Environment.GetEnvironmentVariable(options.Https.CertificatePasswordEnvironmentVariable);
            builder.WebHost.ConfigureKestrel(server => server.ListenAnyIP(options.Https.Port,
                listen => listen.UseHttps(certificatePath, certificatePassword)));
        }
        else
        {
            builder.WebHost.UseUrls(options.ListenUrl);
        }

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddSignalR().AddJsonProtocol(json =>
            json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SqliteAgentStore>();
        builder.Services.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();
        builder.Services.AddSingleton<ICredentialVault, FileCredentialVault>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<CertificateStatusService>();
        builder.Services.AddHostedService(service => service.GetRequiredService<CertificateStatusService>());
        builder.Services.AddSingleton(Ies4224GpProfile.Create());
        builder.Services.AddSingleton<ITelnetClient, TelnetClient>();
        builder.Services.AddSingleton<IDeviceCollector>(service => options.MockMode
            ? new MockDeviceCollector()
            : ActivatorUtilities.CreateInstance<CoreTelnetDeviceCollector>(service));
        builder.Services.AddSingleton<EventPublisher>();
        builder.Services.AddSingleton<CommandExecutionService>();
        builder.Services.AddSingleton<SimulationService>();
        builder.Services.AddHostedService<StoreInitializationService>();
        builder.Services.AddHostedService<PollSchedulerService>();
        builder.Services.AddHostedService<RetentionService>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseMiddleware<BearerTokenMiddleware>();
        app.MapAgentEndpoints(options);
        return app;
    }
}

public sealed class StoreInitializationService(SqliteAgentStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (await AgentMaintenanceCommands.TryRunAsync(args))
        {
            return;
        }
        var app = AgentApplication.Build(args);
        await app.RunAsync();
    }
}

internal static class AgentMaintenanceCommands
{
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 4 && string.Equals(args[0], "credential", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "set", StringComparison.OrdinalIgnoreCase))
        {
            if (Console.IsInputRedirected)
            {
                throw new InvalidOperationException("Credential setup requires an interactive local console.");
            }

            await using var app = AgentApplication.Build([]);
            var password = ReadSecret("Switch password: ");
            try
            {
                var vault = app.Services.GetRequiredService<ICredentialVault>();
                await vault.StoreAsync(args[2], new SwitchCredential(args[3], password));
                Console.WriteLine("Credential was stored with Windows protection. No secret was logged.");
            }
            finally
            {
                password = string.Empty;
            }
            return true;
        }

        if (args.Length == 2 && string.Equals(args[0], "pairing", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "create", StringComparison.OrdinalIgnoreCase))
        {
            await using var app = AgentApplication.Build([]);
            var store = app.Services.GetRequiredService<SqliteAgentStore>();
            await store.InitializeAsync();
            var pairing = app.Services.GetRequiredService<PairingService>();
            var created = await pairing.CreateCodeAsync();
            Console.WriteLine($"One-time pairing code: {created.Code}");
            Console.WriteLine($"Expires (UTC): {created.ExpiresUtc:O}");
            return true;
        }

        return false;
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && value.Length > 0)
            {
                value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }
}
