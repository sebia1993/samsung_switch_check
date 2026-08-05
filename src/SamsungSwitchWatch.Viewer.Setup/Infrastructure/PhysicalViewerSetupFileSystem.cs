using System.Security.Cryptography;
using System.Text;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Infrastructure;

public sealed class PhysicalViewerSetupFileSystem : IViewerSetupFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateTopLevelFiles(string path) =>
        Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);

    public IReadOnlyList<string> EnumerateTopLevelDirectories(string path) =>
        Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);

    public string ReadAllText(string path) =>
        File.ReadAllText(path, new UTF8Encoding(false, true));

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string source, string destination, bool overwrite)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(source, destination, overwrite);
    }

    public void MoveDirectory(string source, string destination) =>
        Directory.Move(source, destination);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void WriteAllTextAtomic(string path, string contents) =>
        WriteAllBytesAtomic(path, new UTF8Encoding(false).GetBytes(contents));

    public void WriteAllBytesAtomic(string path, byte[] contents) =>
        WriteAtomic(path, contents);

    public void EnsureDirectoryWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".viewer-setup-write-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(
                    probe,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathNotWritable,
                "Viewer 설치 또는 작업 기록 폴더에 쓸 수 없습니다.",
                exception);
        }
    }

    public bool DirectoryHasEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path).Any();

    private static void WriteAtomic(string path, byte[] contents)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new IOException("The destination parent directory is missing.");
        }

        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
