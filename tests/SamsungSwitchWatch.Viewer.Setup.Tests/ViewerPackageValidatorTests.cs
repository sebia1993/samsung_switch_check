using System.Text.Json;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerPackageValidatorTests
{
    [Fact]
    public void Validate_AcceptsExactHashedViewerPackage()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();

        var package = new ViewerPackageValidator(workspace.FileSystem)
            .Validate(workspace.PackageDirectory);

        Assert.Equal("0.11.4-poc", package.Version);
        Assert.Contains(package.InstallFiles, file =>
            file.Name == ViewerSetupConstants.ViewerExecutableName);
        Assert.Contains(package.InstallFiles, file =>
            file.Name == ViewerSetupConstants.SetupExecutableName);
    }

    [Fact]
    public void ValidateExisting_AcceptsExactLegacyViewerEntrypoint()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateLegacyInstalledProduct();
        var validator = new ViewerPackageValidator(workspace.FileSystem);

        var package = validator.ValidateExisting(workspace.InstallDirectory);

        Assert.Equal("0.11.3-poc", package.Version);
        Assert.DoesNotContain(package.InstallFiles, file =>
            file.Name == ViewerSetupConstants.SetupExecutableName);
        Assert.Throws<ViewerSetupException>(() =>
            validator.Validate(workspace.InstallDirectory));
    }

    [Fact]
    public void Validate_RejectsChangedFileHash()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(
                workspace.PackageDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "tampered");

        var exception = Assert.Throws<ViewerSetupException>(() =>
            new ViewerPackageValidator(workspace.FileSystem)
                .Validate(workspace.PackageDirectory));

        Assert.Equal(ViewerSetupErrorCodes.PackageHashMismatch, exception.Code);
        Assert.DoesNotContain(workspace.PackageDirectory, exception.Message);
    }

    [Fact]
    public void Validate_RejectsUnlistedTopLevelFile()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(workspace.PackageDirectory, "unexpected.txt"),
            "unexpected");

        var exception = Assert.Throws<ViewerSetupException>(() =>
            new ViewerPackageValidator(workspace.FileSystem)
                .Validate(workspace.PackageDirectory));

        Assert.Equal(ViewerSetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_RejectsUnlistedSubdirectory()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        Directory.CreateDirectory(Path.Combine(workspace.PackageDirectory, "extra"));

        var exception = Assert.Throws<ViewerSetupException>(() =>
            new ViewerPackageValidator(workspace.FileSystem)
                .Validate(workspace.PackageDirectory));

        Assert.Equal(ViewerSetupErrorCodes.ManifestInvalid, exception.Code);
    }

    [Fact]
    public void Validate_RejectsTraversalFileName()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var manifestPath = Path.Combine(
            workspace.PackageDirectory,
            ViewerSetupConstants.ManifestFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var files = root.GetProperty("files")
            .EnumerateArray()
            .Select(item => new
            {
                name = item.GetProperty("name").GetString()!,
                size = item.GetProperty("size").GetInt64(),
                sha256 = item.GetProperty("sha256").GetString()!
            })
            .ToList();
        files[0] = new
        {
            name = "..\\outside.exe",
            files[0].size,
            files[0].sha256
        };
        var replacement = new
        {
            manifestVersion = root.GetProperty("manifestVersion").GetInt32(),
            product = root.GetProperty("product").GetString(),
            packageKind = root.GetProperty("packageKind").GetString(),
            version = root.GetProperty("version").GetString(),
            sourceCommit = root.GetProperty("sourceCommit").GetString(),
            executable = JsonSerializer.Deserialize<object>(
                root.GetProperty("executable").GetRawText()),
            files
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(replacement));

        var exception = Assert.Throws<ViewerSetupException>(() =>
            new ViewerPackageValidator(workspace.FileSystem)
                .Validate(workspace.PackageDirectory));

        Assert.Equal(ViewerSetupErrorCodes.ManifestInvalid, exception.Code);
    }
}
