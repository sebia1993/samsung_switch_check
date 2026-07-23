using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SamsungSwitchWatch.Agent;
using SamsungSwitchWatch.Agent.Execution;

namespace SamsungSwitchWatch.Agent.Tests;

internal sealed class TestAgentHost : IAsyncDisposable
{
    private readonly string _folder;
    private readonly WebApplication _app;

    private TestAgentHost(string folder, WebApplication app, HttpClient client)
    {
        _folder = folder;
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;
    public string DataDirectory => _folder;

    public static async Task<TestAgentHost> StartAsync(
        IStatelessTelnetExecutor? executor = null,
        IReadOnlyDictionary<string, string?>? additionalOverrides = null)
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-AgentTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var overrides = new Dictionary<string, string?>
        {
            ["Agent:ListenUrl"] = "http://127.0.0.1:0",
            ["Agent:DataDirectory"] = folder,
            ["Agent:MockMode"] = "true",
            ["Agent:AllowedTargetCidrs:0"] = "192.0.2.0/24"
        };
        if (additionalOverrides is not null)
        {
            foreach (var item in additionalOverrides)
            {
                overrides[item.Key] = item.Value;
            }
        }

        var app = AgentApplication.Build([], overrides, services =>
        {
            if (executor is not null)
            {
                services.AddSingleton(executor);
            }
        });
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new TestAgentHost(folder, app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
            // A Windows runtime can briefly retain a host-owned file handle.
        }
    }
}
