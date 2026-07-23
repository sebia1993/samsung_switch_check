using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SamsungSwitchWatch.Viewer.Services;

public interface IViewerSecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

/// <summary>
/// Uses Windows DPAPI without a package dependency. The encrypted value can be
/// decrypted only by the same Windows user profile on the same PC.
/// </summary>
public sealed class CurrentUserSecretProtector : IViewerSecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SamsungSwitchWatch.Viewer/v0.8");

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = Transform(plainBytes, protect: true);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainBytes);
            if (protectedBytes is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public string Unprotect(string protectedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedText);
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedText);
            byte[]? plainBytes = null;
            try
            {
                plainBytes = Transform(protectedBytes, protect: false);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedBytes);
                if (plainBytes is not null)
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainBytes);
                }
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("VIEWER_CREDENTIAL_CORRUPT", exception);
        }
    }

    private static byte[] Transform(byte[] source, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required.");
        }

        using var input = DataBlobHandle.FromBytes(source);
        using var entropy = DataBlobHandle.FromBytes(Entropy);
        DataBlob output;
        var success = protect
            ? CryptProtectData(ref input.Blob, null, ref entropy.Blob, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out output)
            : CryptUnprotectData(ref input.Blob, IntPtr.Zero, ref entropy.Blob, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out output);
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "VIEWER_DPAPI_FAILED");

        try
        {
            var result = new byte[output.Size];
            if (output.Size > 0) Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                for (var index = 0; index < output.Size; index++)
                {
                    Marshal.WriteByte(output.Data, index, 0);
                }
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private sealed class DataBlobHandle : IDisposable
    {
        private DataBlobHandle(byte[] value)
        {
            Blob = new DataBlob { Size = value.Length };
            if (value.Length == 0) return;
            Blob.Data = Marshal.AllocHGlobal(value.Length);
            Marshal.Copy(value, 0, Blob.Data, value.Length);
        }

        public DataBlob Blob;
        public static DataBlobHandle FromBytes(byte[] value) => new(value);

        public void Dispose()
        {
            if (Blob.Data == IntPtr.Zero) return;
            for (var index = 0; index < Blob.Size; index++)
            {
                Marshal.WriteByte(Blob.Data, index, 0);
            }
            Marshal.FreeHGlobal(Blob.Data);
            Blob.Data = IntPtr.Zero;
            Blob.Size = 0;
        }
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
