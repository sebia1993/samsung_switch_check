using System.Runtime.InteropServices;
using System.Text;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Infrastructure;

public sealed class WindowsViewerShortcutManager(IViewerSetupFileSystem fileSystem)
    : IViewerShortcutManager
{
    public ShortcutJournalSnapshot Capture(
        string shortcutPath,
        string backupFilePath,
        string expectedTargetPath)
    {
        var existed = fileSystem.FileExists(shortcutPath);
        if (existed)
        {
            fileSystem.WriteAllBytesAtomic(
                backupFilePath,
                fileSystem.ReadAllBytes(shortcutPath));
        }

        return new ShortcutJournalSnapshot(
            shortcutPath,
            existed,
            backupFilePath,
            expectedTargetPath);
    }

    public ViewerShortcutMutationResult Create(
        string shortcutPath,
        string targetPath,
        string workingDirectory)
    {
        var existed = fileSystem.FileExists(shortcutPath);
        if (existed)
        {
            var ownership = ClassifyOwnership(shortcutPath, targetPath);
            if (ownership != ShortcutOwnership.Owned)
            {
                return new ViewerShortcutMutationResult(
                    ownership == ShortcutOwnership.Unowned
                        ? ViewerShortcutMutationStatus.PreservedUnowned
                        : ViewerShortcutMutationStatus.OwnershipUnknown);
            }
        }

        var parent = Path.GetDirectoryName(shortcutPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new IOException("The shortcut parent directory is missing.");
        }

        fileSystem.CreateDirectory(parent);
        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)(object)new ShellLinkComObject();
            shellLink.SetPath(targetPath);
            shellLink.SetWorkingDirectory(workingDirectory);
            shellLink.SetIconLocation(targetPath, 0);
            shellLink.SetDescription("Samsung Switch Watch Viewer");
            ((IPersistFile)shellLink).Save(shortcutPath, remember: true);
        }
        finally
        {
            ReleaseCom(shellLink);
        }

        if (!fileSystem.FileExists(shortcutPath))
        {
            throw new IOException("The shortcut was not created.");
        }

        return new ViewerShortcutMutationResult(
            existed
                ? ViewerShortcutMutationStatus.UpdatedOwned
                : ViewerShortcutMutationStatus.Created);
    }

    public ViewerShortcutMutationResult RemoveOwned(
        string shortcutPath,
        string expectedTargetPath)
    {
        if (!fileSystem.FileExists(shortcutPath))
        {
            return new ViewerShortcutMutationResult(
                ViewerShortcutMutationStatus.Missing);
        }

        var ownership = ClassifyOwnership(shortcutPath, expectedTargetPath);
        if (ownership != ShortcutOwnership.Owned)
        {
            return new ViewerShortcutMutationResult(
                ownership == ShortcutOwnership.Unowned
                    ? ViewerShortcutMutationStatus.PreservedUnowned
                    : ViewerShortcutMutationStatus.OwnershipUnknown);
        }

        fileSystem.DeleteFile(shortcutPath);
        return new ViewerShortcutMutationResult(
            ViewerShortcutMutationStatus.RemovedOwned);
    }

    public void Restore(ShortcutJournalSnapshot snapshot)
    {
        if (fileSystem.FileExists(snapshot.ShortcutPath))
        {
            var ownership = ClassifyOwnership(
                snapshot.ShortcutPath,
                snapshot.ExpectedTargetPath);
            if (ownership != ShortcutOwnership.Owned)
            {
                return;
            }
        }

        if (!snapshot.Existed)
        {
            fileSystem.DeleteFile(snapshot.ShortcutPath);
            return;
        }

        if (!fileSystem.FileExists(snapshot.BackupFilePath))
        {
            throw new IOException("The shortcut backup is missing.");
        }

        fileSystem.WriteAllBytesAtomic(
            snapshot.ShortcutPath,
            fileSystem.ReadAllBytes(snapshot.BackupFilePath));
    }

    private static ShortcutOwnership ClassifyOwnership(
        string shortcutPath,
        string expectedTargetPath)
    {
        if (!TryReadTargetPath(shortcutPath, out var targetPath))
        {
            return ShortcutOwnership.Unknown;
        }

        try
        {
            return string.Equals(
                    Path.GetFullPath(targetPath),
                    Path.GetFullPath(expectedTargetPath),
                    StringComparison.OrdinalIgnoreCase)
                ? ShortcutOwnership.Owned
                : ShortcutOwnership.Unowned;
        }
        catch (ArgumentException)
        {
            return ShortcutOwnership.Unknown;
        }
    }

    private static bool TryReadTargetPath(
        string shortcutPath,
        out string targetPath)
    {
        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)(object)new ShellLinkComObject();
            ((IPersistFile)shellLink).Load(shortcutPath, 0);
            var buffer = new StringBuilder(32768);
            var result = shellLink.GetPath(
                buffer,
                buffer.Capacity,
                IntPtr.Zero,
                4);
            targetPath = buffer.ToString();
            return result >= 0 && !string.IsNullOrWhiteSpace(targetPath);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidCastException)
        {
            targetPath = string.Empty;
            return false;
        }
        finally
        {
            ReleaseCom(shellLink);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private enum ShortcutOwnership
    {
        Owned,
        Unowned,
        Unknown
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkComObject;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumPath,
            IntPtr findData,
            uint flags);

        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,
            int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maximumPath);
        void SetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int maximumPath,
            out int iconIndex);
        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            int iconIndex);
        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            uint mode);

        void Save(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile(
            [MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
