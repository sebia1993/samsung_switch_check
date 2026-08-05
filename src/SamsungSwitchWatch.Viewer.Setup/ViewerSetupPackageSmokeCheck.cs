using System.Windows;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup;

internal static class ViewerSetupPackageSmokeCheck
{
    internal const string Argument = "--package-smoke-check";
    internal const int SuccessExitCode = 0;
    internal const int ApplicationResourceFailureExitCode = 40;
    internal const int ScreenResourceFailureExitCode = 41;
    internal const int PackageFileFailureExitCode = 42;
    internal const int UnexpectedFailureExitCode = 43;

    private static readonly object[] RequiredApplicationResources =
    [
        "CanvasBrush",
        "SurfaceBrush",
        "TextBrush",
        "MutedTextBrush",
        "BorderBrush",
        "PrimaryBrush",
        "PrimaryHoverBrush",
        "CardStyle",
        "PrimaryButton",
        "SecondaryButton"
    ];

    private static readonly Uri[] RequiredScreenResources =
    [
        new(
            "/SamsungSwitchWatch.Viewer.Setup;component/MainWindow.xaml",
            UriKind.Relative)
    ];

    private static readonly string[] RequiredPackageFiles =
    [
        ViewerSetupConstants.ViewerExecutableName,
        ViewerSetupConstants.SetupExecutableName,
        ViewerSetupConstants.ManifestFileName
    ];

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 &&
        string.Equals(arguments[0], Argument, StringComparison.Ordinal);

    internal static int Run(
        string packageDirectory,
        Func<object, object?> getApplicationResource,
        Func<Uri, Stream?>? openScreenResource = null,
        Func<string, bool>? fileExists = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                return PackageFileFailureExitCode;
            }

            foreach (var key in RequiredApplicationResources)
            {
                if (getApplicationResource(key) is null)
                {
                    return ApplicationResourceFailureExitCode;
                }
            }

            openScreenResource ??= OpenApplicationResource;
            foreach (var resourceUri in RequiredScreenResources)
            {
                using var stream = openScreenResource(resourceUri);
                if (stream is null || !stream.CanRead || stream.ReadByte() < 0)
                {
                    return ScreenResourceFailureExitCode;
                }
            }

            fileExists ??= File.Exists;
            return RequiredPackageFiles.All(file =>
                    fileExists(Path.Combine(packageDirectory, file)))
                ? SuccessExitCode
                : PackageFileFailureExitCode;
        }
        catch
        {
            return UnexpectedFailureExitCode;
        }
    }

    private static Stream? OpenApplicationResource(Uri resourceUri) =>
        Application.GetResourceStream(resourceUri)?.Stream;
}
