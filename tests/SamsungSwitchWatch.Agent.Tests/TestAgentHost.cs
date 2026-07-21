using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SamsungSwitchWatch.Agent;
using SamsungSwitchWatch.Agent.Polling;

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

    public static async Task<TestAgentHost> StartAsync(
        IDeviceCollector? collector = null,
        IReadOnlyDictionary<string, string?>? additionalOverrides = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-AgentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var overrides = new Dictionary<string, string?>
        {
            ["Agent:ListenUrl"] = "http://127.0.0.1:0",
            ["Agent:DataDirectory"] = folder,
            ["Agent:MockMode"] = "true",
            ["Agent:EnablePolling"] = "false",
            ["Agent:EnableSimulator"] = "true",
            ["Agent:TokenPepper"] = Guid.NewGuid().ToString("N")
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
            if (collector is not null)
            {
                services.AddSingleton(collector);
            }
        });
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new TestAgentHost(folder, app, client);
    }

    public async Task<string> PairAsync()
    {
        using var bootstrap = await Client.PostAsync("/api/v1/pairing/bootstrap", null);
        bootstrap.EnsureSuccessStatusCode();
        using var bootstrapJson = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
        var code = bootstrapJson.RootElement.GetProperty("code").GetString()!;
        using var exchange = await Client.PostAsJsonAsync("/api/v1/pairing/exchange", new { code });
        exchange.EnsureSuccessStatusCode();
        using var tokenJson = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync());
        var token = tokenJson.RootElement.GetProperty("token").GetString()!;
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
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
            // SQLite can briefly retain a file handle on Windows after host shutdown.
        }
    }
}
