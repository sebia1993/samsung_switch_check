using System.Text;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class ExistingTargetNetworkLoaderTests
{
    [Fact]
    public void Load_ReturnsEmptySuccessWhenConfigurationDoesNotExist()
    {
        using var folder = new TemporaryFolder();
        var paths = CreatePaths(folder);
        var loader = new ExistingTargetNetworkLoader(new TestFileSystem(), paths);

        var result = loader.Load();

        Assert.True(result.Succeeded);
        Assert.Empty(result.TargetCidrs);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData(
        """{"Agent":{"AllowedTargetCidrs":["10.20.0.0/16"]}}""",
        "10.20.0.0/16")]
    [InlineData(
        """{"Agent":{"AllowedTargetCidrs":["172.16.0.0/12","192.168.50.0/24"]}}""",
        "172.16.0.0/12",
        "192.168.50.0/24")]
    public void Load_ReturnsCanonicalPrivateNetworks(
        string configuration,
        params string[] expected)
    {
        using var folder = new TemporaryFolder();
        var paths = CreatePaths(folder);
        WriteConfiguration(paths, configuration);
        var loader = new ExistingTargetNetworkLoader(new TestFileSystem(), paths);

        var result = loader.Load();

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.TargetCidrs);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not-json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"Agent":"wrong-type"}""")]
    [InlineData("""{"Agent":{}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":null}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":"10.0.0.0/8"}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":[]}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":["10.0.0.0/8","172.16.0.0/12","192.168.0.0/16"]}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":[10]}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":["10.0.0.0/8","10.0.0.0/8"]}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":["10.20.30.40/16"]}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":["8.8.8.0/24"]}}""")]
    [InlineData("""{"Agent":{"AllowedTargetCidrs":[" 10.0.0.0/8"]}}""")]
    public void Load_ReturnsSanitizedWarningForInvalidConfiguration(string configuration)
    {
        using var folder = new TemporaryFolder();
        var paths = CreatePaths(folder);
        WriteConfiguration(paths, configuration);
        var loader = new ExistingTargetNetworkLoader(new TestFileSystem(), paths);

        var result = loader.Load();

        AssertWarning(result);
        Assert.Equal(
            "기존 Agent 관리망을 불러오지 못했습니다. 승인된 관리망을 다시 선택하거나 추가하세요.",
            result.Warning!.Message);
    }

    [Fact]
    public void Load_ReturnsSanitizedWarningWhenConfigurationReadFails()
    {
        using var folder = new TemporaryFolder();
        var paths = CreatePaths(folder);
        var loader = new ExistingTargetNetworkLoader(new ReadFailingFileSystem(), paths);

        var result = loader.Load();

        AssertWarning(result);
    }

    private static DeploymentPaths CreatePaths(TemporaryFolder folder) =>
        new(
            folder.Combine("package"),
            folder.Combine("install"),
            folder.Combine("data"),
            folder.Combine("operations"));

    private static void WriteConfiguration(
        DeploymentPaths paths,
        string configuration)
    {
        Directory.CreateDirectory(paths.InstallDirectory);
        File.WriteAllText(
            paths.ProductionConfigurationPath,
            configuration,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AssertWarning(ExistingTargetNetworksLoadResult result)
    {
        Assert.False(result.Succeeded);
        Assert.Empty(result.TargetCidrs);
        Assert.NotNull(result.Warning);
        Assert.Equal(
            SetupErrorCodes.ExistingNetworksNotLoaded,
            result.Warning.Code);
        Assert.Equal(SetupStepState.Warning, result.Warning.State);
    }

    private sealed class ReadFailingFileSystem : ISetupFileSystem
    {
        public bool FileExists(string path) => true;
        public string ReadAllText(string path) =>
            throw new IOException("simulated sensitive read failure");

        public bool DirectoryExists(string path) => throw new NotSupportedException();
        public void WriteAllTextAtomic(string path, string contents) =>
            throw new NotSupportedException();
        public string ComputeSha256(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void CopyFile(string source, string destination, bool overwrite) =>
            throw new NotSupportedException();
        public void MoveDirectory(string source, string destination) =>
            throw new NotSupportedException();
        public void DeleteDirectory(string path, bool recursive) =>
            throw new NotSupportedException();
        public void DeleteFile(string path) => throw new NotSupportedException();
        public void EnsureDirectoryAccess(string path, DirectoryAccessKind accessKind) =>
            throw new NotSupportedException();
        public bool CanCreateUnder(string path) => throw new NotSupportedException();
        public void ValidateDeploymentPaths(
            DeploymentPaths paths,
            ServiceSnapshot service,
            IReadOnlyList<string> transactionPaths) =>
            throw new NotSupportedException();
        public void ValidateRecoveryPaths(
            DeploymentPaths paths,
            ServiceSnapshot currentService,
            ServiceSnapshot previousService,
            bool allowFreshCreatedDataCleanup,
            IReadOnlyList<string> transactionPaths) =>
            throw new NotSupportedException();
    }
}
