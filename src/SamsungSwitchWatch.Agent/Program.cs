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
        WebApplication? app = null;

        try
        {
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
            // Register through a factory so the DI container owns and disposes the
            // process-lifetime certificate after Kestrel has stopped using it.
            // AddSingleton(instance) treats the object as externally owned and
            // would leave the temporary Windows user-key container behind.
            builder.Services.AddSingleton<AgentIdentity>(_ => identity);
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

            app = builder.Build();
            // Kestrel receives the certificate during builder configuration and
            // the identity API might never be called. Resolve the factory-backed
            // singleton now so the host tracks its disposal on every shutdown.
            _ = app.Services.GetRequiredService<AgentIdentity>();
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
            app.UseMiddleware<ViewerIpAccessMiddleware>();
            app.MapAgentEndpoints(options);
            return app;
        }
        catch (Exception buildException)
        {
            try
            {
                if (app is null)
                {
                    identity.Dispose();
                }
                else
                {
                    app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception disposeException)
            {
                throw new AggregateException(
                    "Agent initialization and identity cleanup both failed.",
                    buildException,
                    disposeException);
            }

            throw;
        }
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
