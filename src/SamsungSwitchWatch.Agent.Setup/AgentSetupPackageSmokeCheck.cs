using System.Windows;

namespace SamsungSwitchWatch.Agent.Setup;

internal static class AgentSetupPackageSmokeCheck
{
    internal const string Argument = "--package-smoke-check";
    internal const int SuccessExitCode = 0;
    internal const int ApplicationResourceFailureExitCode = 30;
    internal const int ScreenResourceFailureExitCode = 31;
    internal const int PackageFileFailureExitCode = 32;
    internal const int UnexpectedFailureExitCode = 33;

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
            "/SamsungSwitchWatch.Agent.Setup;component/MainWindow.xaml",
            UriKind.Relative)
    ];

    private static readonly string[] RequiredPackageFiles =
    [
        "SamsungSwitchWatch.Agent.exe",
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "PresentationNative_cor3.dll",
        "vcruntime140_cor3.dll",
        "wpfgfx_cor3.dll",
        "BUILD-MANIFEST.json"
    ];

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count == 1
        && string.Equals(arguments[0], Argument, StringComparison.Ordinal);

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
            foreach (var fileName in RequiredPackageFiles)
            {
                if (!fileExists(Path.Combine(packageDirectory, fileName)))
                {
                    return PackageFileFailureExitCode;
                }
            }

            return SuccessExitCode;
        }
        catch
        {
            return UnexpectedFailureExitCode;
        }
    }

    private static Stream? OpenApplicationResource(Uri resourceUri) =>
        Application.GetResourceStream(resourceUri)?.Stream;
}
