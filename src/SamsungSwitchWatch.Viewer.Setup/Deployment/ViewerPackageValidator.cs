using System.Text.Json;

namespace SamsungSwitchWatch.Viewer.Setup.Deployment;

public sealed class ViewerPackageValidator(IViewerSetupFileSystem fileSystem)
    : IViewerPackageValidator
{
    private const int MaximumManifestCharacters = 2 * 1024 * 1024;

    public ViewerPackage Validate(string packageDirectory) =>
        ValidateCore(packageDirectory, requireCurrentSetupEntrypoint: true);

    public ViewerPackage ValidateExisting(string installDirectory) =>
        ValidateCore(installDirectory, requireCurrentSetupEntrypoint: false);

    private ViewerPackage ValidateCore(
        string packageDirectory,
        bool requireCurrentSetupEntrypoint)
    {
        string packageRoot;
        try
        {
            packageRoot = Path.GetFullPath(packageDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "Viewer 패키지 경로가 올바르지 않습니다.",
                exception);
        }

        var viewerPath = Path.Combine(
            packageRoot,
            ViewerSetupConstants.ViewerExecutableName);
        var setupPath = Path.Combine(
            packageRoot,
            ViewerSetupConstants.SetupExecutableName);
        var manifestPath = Path.Combine(
            packageRoot,
            ViewerSetupConstants.ManifestFileName);

        if (!fileSystem.FileExists(viewerPath) ||
            requireCurrentSetupEntrypoint && !fileSystem.FileExists(setupPath) ||
            !fileSystem.FileExists(manifestPath))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PackageNotFound,
                "Viewer 실행 파일, 설치 프로그램 또는 BUILD-MANIFEST.json이 없습니다.");
        }

        BuildManifest? manifest;
        string manifestSha256;
        try
        {
            if (fileSystem.GetFileLength(manifestPath) > MaximumManifestCharacters)
            {
                throw new JsonException();
            }

            var beforeReadHash = fileSystem.ComputeSha256(manifestPath);
            var json = fileSystem.ReadAllText(manifestPath);
            manifestSha256 = fileSystem.ComputeSha256(manifestPath);
            if (!string.Equals(
                    beforeReadHash,
                    manifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The package manifest changed while it was read.");
            }

            if (json.Length > MaximumManifestCharacters)
            {
                throw new JsonException();
            }

            manifest = JsonSerializer.Deserialize<BuildManifest>(json, JsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.ManifestInvalid,
                "BUILD-MANIFEST.json 형식을 확인할 수 없습니다.",
                exception);
        }

        if (manifest is null ||
            manifest.ManifestVersion != 1 ||
            !string.Equals(
                manifest.Product,
                ViewerSetupConstants.ProductName,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.PackageKind, "Viewer", StringComparison.Ordinal) ||
            !IsSafeVersion(manifest.Version) ||
            !IsCommit(manifest.SourceCommit) ||
            manifest.Executable is null ||
            !IsAcceptedExecutableName(
                manifest.Executable.Name,
                requireCurrentSetupEntrypoint) ||
            !IsSha256(manifest.Executable.Sha256) ||
            manifest.Files is null ||
            manifest.Files.Count == 0)
        {
            throw InvalidManifest();
        }

        var verified = new List<ViewerPackageFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.Name) ||
                !IsSafeFileName(entry.Name) ||
                !seen.Add(entry.Name) ||
                !IsSha256(entry.Sha256) ||
                entry.Size < 0)
            {
                throw InvalidManifest();
            }

            var path = Path.Combine(packageRoot, entry.Name);
            if (!fileSystem.FileExists(path))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.PackageNotFound,
                    $"Viewer 패키지 필수 파일이 없습니다: {entry.Name}");
            }

            if (fileSystem.GetFileLength(path) != entry.Size ||
                !string.Equals(
                    fileSystem.ComputeSha256(path),
                    entry.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.PackageHashMismatch,
                    $"Viewer 패키지 파일 무결성 확인에 실패했습니다: {entry.Name}");
            }

            verified.Add(new ViewerPackageFile(
                entry.Name,
                path,
                entry.Sha256!.ToLowerInvariant(),
                entry.Size));
        }

        if (!seen.Contains(ViewerSetupConstants.ViewerExecutableName) ||
            requireCurrentSetupEntrypoint &&
            !seen.Contains(ViewerSetupConstants.SetupExecutableName) ||
            !seen.Contains(manifest.Executable.Name!))
        {
            throw InvalidManifest();
        }

        var actualNames = fileSystem
            .EnumerateTopLevelFiles(packageRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var expectedNames = seen.ToHashSet(StringComparer.Ordinal);
        expectedNames.Add(ViewerSetupConstants.ManifestFileName);
        if (!actualNames.SetEquals(expectedNames) ||
            fileSystem.EnumerateTopLevelDirectories(packageRoot).Count != 0)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.ManifestInvalid,
                "Viewer 패키지 파일 목록이 빌드 목록과 일치하지 않습니다.");
        }

        var executable = verified.Single(file => string.Equals(
            file.Name,
            manifest.Executable.Name,
            StringComparison.Ordinal));
        if (!string.Equals(
                executable.Sha256,
                manifest.Executable.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PackageHashMismatch,
                "Viewer 설치 프로그램 무결성 확인에 실패했습니다.");
        }

        return new ViewerPackage(
            manifest.Version!,
            manifest.SourceCommit!,
            manifestPath,
            manifestSha256,
            verified);
    }

    private static bool IsSafeFileName(string value) =>
        Path.GetFileName(value) == value &&
        value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsAcceptedExecutableName(
        string? value,
        bool requireCurrentSetupEntrypoint) =>
        string.Equals(
            value,
            ViewerSetupConstants.SetupExecutableName,
            StringComparison.Ordinal) ||
        !requireCurrentSetupEntrypoint &&
        string.Equals(
            value,
            ViewerSetupConstants.ViewerExecutableName,
            StringComparison.Ordinal);

    private static bool IsCommit(string? value) =>
        value?.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool IsSafeVersion(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '.' or '-' or '+' or '_');

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static ViewerSetupException InvalidManifest() =>
        new(
            ViewerSetupErrorCodes.ManifestInvalid,
            "Viewer 빌드 정보가 누락되었거나 지원하지 않는 형식입니다.");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class BuildManifest
    {
        public int ManifestVersion { get; init; }
        public string? Product { get; init; }
        public string? PackageKind { get; init; }
        public string? Version { get; init; }
        public string? SourceCommit { get; init; }
        public ExecutableManifest? Executable { get; init; }
        public List<FileManifest?>? Files { get; init; }
    }

    private sealed class ExecutableManifest
    {
        public string? Name { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class FileManifest
    {
        public string? Name { get; init; }
        public string? Sha256 { get; init; }
        public long Size { get; init; }
    }
}
