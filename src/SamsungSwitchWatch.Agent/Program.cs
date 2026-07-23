using System.Text.Json.Serialization;
using SamsungSwitchWatch.Agent.Api;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Execution;
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
        var serviceMode = args.Any(value =>
            string.Equals(value, "--service", StringComparison.OrdinalIgnoreCase));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        if (serviceMode)
        {
            builder.Host.UseWindowsService(options =>
                options.ServiceName = "SamsungSwitchWatchAgent");
        }
        if (overrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        var options =
            builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ??
            new AgentOptions();
        AgentOptionsValidator.ValidateAndNormalize(options, builder.Environment.ContentRootPath);
        var identity = AgentIdentityStore.LoadOrCreate(options);

        builder.WebHost.UseUrls(options.ListenUrl);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            if (new Uri(options.ListenUrl).Scheme == Uri.UriSchemeHttps)
            {
                kestrel.ConfigureHttpsDefaults(https =>
                    https.ServerCertificate = identity.Certificate);
            }
        });

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow;
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(new DeviceProfileRegistry(
        [
            Ies4224GpProfile.Create(),
            Ies4028XpProfile.Create(),
            Ies4226XpProfile.Create()
        ]));
        builder.Services.AddSingleton<TargetNetworkPolicy>();
        builder.Services.AddSingleton<TelnetExecutionAdmission>();
        builder.Services.AddSingleton<IAdHocTelnetClient>(_ => new TelnetClient(
            options: new TelnetClientOptions(
                TelnetTimeouts.Default with
                {
                    Session = TimeSpan.FromSeconds(options.Telnet.MaxSessionSeconds)
                })
            {
                SessionCloseRetryCount = options.Telnet.ImmediateSessionCloseRetryCount,
                SessionCloseRetryDelay =
                    TimeSpan.FromSeconds(options.Telnet.ImmediateSessionCloseRetryDelaySeconds)
            }));
        builder.Services.AddSingleton<IStatelessTelnetExecutor>(services =>
            options.MockMode
                ? new MockStatelessTelnetExecutor()
                : ActivatorUtilities.CreateInstance<CoreStatelessTelnetExecutor>(services));
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseStatusCodePages(async statusContext =>
        {
            var response = statusContext.HttpContext.Response;
            if (response.StatusCode is
                    StatusCodes.Status400BadRequest or
                    StatusCodes.Status413PayloadTooLarge &&
                response.ContentLength is null &&
                string.IsNullOrEmpty(response.ContentType))
            {
                var tooLarge =
                    response.StatusCode == StatusCodes.Status413PayloadTooLarge;
                await response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = tooLarge
                            ? Domain.AgentErrorCodes.RequestTooLarge
                            : Domain.AgentErrorCodes.RequestInvalid,
                        message = tooLarge
                            ? "Request body exceeds the Agent safety limit."
                            : "Request body is invalid."
                    }
                });
            }
        });
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.MapAgentEndpoints(options);
        return app;
    }
}

public static class Program
{
    public static async Task Main(string[] args)
    {
        var runtimeMode = args.Any(value =>
            string.Equals(value, "--service", StringComparison.OrdinalIgnoreCase));
        if (!runtimeMode)
        {
            return;
        }

        var app = AgentApplication.Build(args);
        await app.RunAsync();
    }
}
