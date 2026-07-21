using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungSwitchWatch.Agent.Api;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
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
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Host.UseWindowsService(options => options.ServiceName = "SamsungSwitchWatchAgent");
        if (overrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        var options = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
        AgentOptionsValidator.ValidateAndNormalize(options, builder.Environment.ContentRootPath);

        if (options.Https.Enabled)
        {
            builder.WebHost.ConfigureKestrel(server => server.ListenAnyIP(options.Https.Port,
                listen => listen.UseHttps(AgentCertificateLoader.Load(options.Https,
                    builder.Environment.ContentRootPath))));
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
        builder.Services.AddSingleton<AgentRuntimeState>();
        builder.Services.AddSingleton<AgentReadinessService>();
        builder.Services.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();
        builder.Services.AddSingleton<IRawOutputProtector, RawOutputProtector>();
        builder.Services.AddSingleton<ICredentialVault, FileCredentialVault>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<PairingAttemptLimiter>();
        builder.Services.AddSingleton<CertificateStatusService>();
        builder.Services.AddHostedService(service => service.GetRequiredService<CertificateStatusService>());
        builder.Services.AddSingleton(new DeviceProfileRegistry(
        [
            Ies4224GpProfile.Create(),
            Ies4028XpProfile.Create(),
            Ies4226XpProfile.Create()
        ]));
        builder.Services.AddSingleton<ITelnetClient>(_ => new TelnetClient(options: new TelnetClientOptions(
            TelnetTimeouts.Default with
            {
                Session = TimeSpan.FromSeconds(options.Telnet.MaxSessionSeconds)
            })
        {
            SessionCloseRetryCount = options.Telnet.ImmediateSessionCloseRetryCount,
            SessionCloseRetryDelay = TimeSpan.FromSeconds(options.Telnet.ImmediateSessionCloseRetryDelaySeconds)
        }));
        builder.Services.AddSingleton<IDeviceCollector>(service => options.MockMode
            ? new MockDeviceCollector()
            : ActivatorUtilities.CreateInstance<CoreTelnetDeviceCollector>(service));
        builder.Services.AddSingleton<EventPublisher>();
        builder.Services.AddSingleton<CommandExecutionService>();
        builder.Services.AddSingleton<SimulationService>();
        builder.Services.AddHostedService<StoreInitializationService>();
        builder.Services.AddHostedService<StorageIntegrityService>();
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

public sealed class StoreInitializationService(
    SqliteAgentStore store,
    ILogger<StoreInitializationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.InitializeAsync(cancellationToken);
        }
        catch (AgentOperationException ex)
        {
            logger.LogError("Agent storage is not ready with {ErrorCode}; liveness remains available.", ex.Code);
        }
    }
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

        if (args.Length is 2 or 3 && string.Equals(args[0], "pairing", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "create", StringComparison.OrdinalIgnoreCase) &&
            (args.Length == 2 || string.Equals(args[2], "--json", StringComparison.OrdinalIgnoreCase)))
        {
            var options = AgentMaintenanceBootstrap.LoadOptions();
            var created = await AgentMaintenanceBootstrap.CreatePairingCodeAsync(options);
            if (args.Length == 3)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    code = created.Code,
                    expiresUtc = created.ExpiresUtc
                }, JsonDefaults.Serializer));
            }
            else
            {
                Console.WriteLine($"One-time pairing code: {created.Code}");
                Console.WriteLine($"Expires (UTC): {created.ExpiresUtc:O}");
            }
            return true;
        }

        if (args.Length == 2 && string.Equals(args[0], "token", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            await using var app = AgentApplication.Build([]);
            var store = app.Services.GetRequiredService<SqliteAgentStore>();
            await store.InitializeAsync();
            var pairing = app.Services.GetRequiredService<PairingService>();
            var tokens = await pairing.ListTokensAsync();
            if (tokens.Count == 0)
            {
                Console.WriteLine("No Viewer tokens are registered.");
                return true;
            }
            foreach (var token in tokens)
            {
                var state = token.RevokedUtc is not null ? "revoked" : token.Expired ? "expired" : "active";
                Console.WriteLine($"{token.Id}  {state}  created={token.CreatedUtc:O}  last-used={token.LastUsedUtc:O}");
            }
            return true;
        }

        if (args.Length == 3 && string.Equals(args[0], "token", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "revoke", StringComparison.OrdinalIgnoreCase))
        {
            await using var app = AgentApplication.Build([]);
            var store = app.Services.GetRequiredService<SqliteAgentStore>();
            await store.InitializeAsync();
            await app.Services.GetRequiredService<PairingService>().RevokeTokenAsync(args[2]);
            Console.WriteLine($"Viewer token {args[2].ToUpperInvariant()} was revoked.");
            return true;
        }

        if (args.Length == 3 && string.Equals(args[0], "token", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "rotate", StringComparison.OrdinalIgnoreCase))
        {
            await using var app = AgentApplication.Build([]);
            var store = app.Services.GetRequiredService<SqliteAgentStore>();
            await store.InitializeAsync();
            var replacement = await app.Services.GetRequiredService<PairingService>().RotateTokenAsync(args[2]);
            Console.WriteLine("Replacement Viewer token (shown once):");
            Console.WriteLine(replacement);
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
                if (value.Length >= 512)
                {
                    throw new InvalidOperationException("Switch password cannot exceed 512 characters.");
                }
                value.Append(key.KeyChar);
            }
        }
    }
}

internal static class AgentMaintenanceBootstrap
{
    public static AgentOptions LoadOptions(
        string? contentRootPath = null,
        string? environmentName = null)
    {
        var contentRoot = Path.GetFullPath(contentRootPath ?? AppContext.BaseDirectory);
        var environment = string.IsNullOrWhiteSpace(environmentName)
            ? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
              ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
              ?? Environments.Production
            : environmentName.Trim();

        if (environment.Length > 64 ||
            environment.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Agent environment name is invalid.");
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        var options = configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
        AgentOptionsValidator.ValidateAndNormalize(options, contentRoot);
        return options;
    }

    public static async Task<(string Code, DateTimeOffset ExpiresUtc)> CreatePairingCodeAsync(
        AgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
        await store.InitializeAsync(cancellationToken);
        var pairing = new PairingService(options, store);
        return await pairing.CreateCodeAsync(cancellationToken);
    }
}
