using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class AgentPackageValidatorTests
{
    [Fact]
    public void Validate_AcceptsSetupPrimaryAndAgentRuntimeEntries()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var fileSystem = new TestFileSystem();

        var result = new AgentPackageValidator(fileSystem).Validate(package);

        Assert.Equal("0.10.0-poc", result.Version);
        Assert.Equal(
            Path.Combine(package, SetupConstants.AgentExecutableName),
            result.ExecutablePath);
        Assert.Contains(
            result.VerifiedFiles,
            file => file.Name == SetupConstants.SetupExecutableName);
        Assert.Contains(
            result.VerifiedFiles,
            file => file.Name == "agent-companion.dll");
    }

    [Fact]
    public void Validate_RejectsMissingCompanionListedByManifest()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        File.Delete(Path.Combine(package, "agent-companion.dll"));

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.PackageNotFound, exception.Code);
    }

    [Fact]
    public void Validate_RejectsTamperedAgentExecutable()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        File.AppendAllText(
            Path.Combine(package, SetupConstants.AgentExecutableName),
            "tampered");

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.PackageHashMismatch, exception.Code);
    }

    [Fact]
    public void Validate_RejectsAgentAsPrimaryExecutable()
    {
        using var folder = new TemporaryFolder();
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var manifestPath = Path.Combine(package, SetupConstants.ManifestFileName);
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        root["executable"]!["name"] = SetupConstants.AgentExecutableName;
        File.WriteAllText(
            manifestPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = Assert.Throws<SetupException>(
            () => new AgentPackageValidator(new TestFileSystem()).Validate(package));

        Assert.Equal(SetupErrorCodes.ManifestInvalid, exception.Code);
    }
}
