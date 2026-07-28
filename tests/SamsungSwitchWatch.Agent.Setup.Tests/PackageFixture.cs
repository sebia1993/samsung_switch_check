using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

internal static class PackageFixture
{
    public static void Create(
        string packageDirectory,
        string agentContents = "new-agent",
        string setupContents = "native-setup",
        bool includeCompanion = true)
    {
        Directory.CreateDirectory(packageDirectory);
        Write(Path.Combine(packageDirectory, SetupConstants.AgentExecutableName), agentContents);
        Write(Path.Combine(packageDirectory, SetupConstants.SetupExecutableName), setupContents);
        if (includeCompanion)
        {
            Write(Path.Combine(packageDirectory, "agent-companion.dll"), "companion");
        }

        var names = new List<string>
        {
            SetupConstants.SetupExecutableName,
            SetupConstants.AgentExecutableName
        };
        if (includeCompanion)
        {
            names.Add("agent-companion.dll");
        }

        var files = names.Select(name =>
        {
            var path = Path.Combine(packageDirectory, name);
            return new
            {
                name,
                size = new FileInfo(path).Length,
                sha256 = Hash(path)
            };
        }).ToArray();
        var setupPath = Path.Combine(packageDirectory, SetupConstants.SetupExecutableName);
        var manifest = new
        {
            manifestVersion = 1,
            product = "SamsungSwitchWatch",
            packageKind = "Agent",
            version = "0.10.0-poc",
            sourceCommit = new string('a', 40),
            executable = new
            {
                name = SetupConstants.SetupExecutableName,
                sha256 = Hash(setupPath),
                productVersion = "0.10.0-poc+" + new string('a', 40)
            },
            files
        };
        File.WriteAllText(
            Path.Combine(packageDirectory, SetupConstants.ManifestFileName),
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(false));
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Write(string path, string value) =>
        File.WriteAllText(path, value, new UTF8Encoding(false));
}
