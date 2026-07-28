using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Infrastructure;

public sealed partial class HttpsAgentHealthProbe : IAgentHealthProbe
{
    private const int MaximumIdentityBytes = 16 * 1024;
    private const int AddressFamilyIpv4 = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;

    public async Task<bool> WaitUntilReadyAsync(
        Uri endpoint,
        string? expectedProductVersion,
        int expectedProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The native Setup health probe is restricted to loopback HTTPS.",
                nameof(endpoint));
        }

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var identityEndpoint = new Uri(endpoint, "/api/v4/identity");

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                if (expectedProcessId > 0 &&
                    !OwnsListeningPort(expectedProcessId, endpoint.Port))
                {
                    await DelayBeforeRetry(deadline.Token, cancellationToken);
                    continue;
                }

                using var readyResponse = await client.GetAsync(endpoint, deadline.Token);
                if (readyResponse.StatusCode != HttpStatusCode.OK)
                {
                    await DelayBeforeRetry(deadline.Token, cancellationToken);
                    continue;
                }

                if (expectedProductVersion is null)
                {
                    return true;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, identityEndpoint);
                using var identityResponse = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token);
                if (identityResponse.StatusCode != HttpStatusCode.OK ||
                    identityResponse.Content.Headers.ContentLength > MaximumIdentityBytes)
                {
                    await DelayBeforeRetry(deadline.Token, cancellationToken);
                    continue;
                }

                var identityJson = await ReadBoundedAsync(
                    identityResponse.Content,
                    MaximumIdentityBytes,
                    deadline.Token);
                if (identityJson is not null &&
                    IsExpectedIdentity(identityJson, expectedProductVersion))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (deadline.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // The listener can start after the service process. Retry while bounded.
            }
            catch (JsonException)
            {
                // A non-Agent or incomplete identity payload is never accepted.
            }

            await DelayBeforeRetry(deadline.Token, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    internal static bool IsExpectedIdentity(string json, string expectedProductVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("apiVersion", out var apiVersion) &&
               apiVersion.ValueKind == JsonValueKind.Number &&
               apiVersion.TryGetInt32(out var api) &&
               api == 4 &&
               root.TryGetProperty("protocol", out var protocol) &&
               string.Equals(
                   protocol.GetString(),
                   "https",
                   StringComparison.Ordinal) &&
               root.TryGetProperty("productVersion", out var version) &&
               string.Equals(
                   NormalizeVersion(version.GetString()),
                   NormalizeVersion(expectedProductVersion),
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var normalized = version.Trim();
        var metadata = normalized.IndexOf('+');
        return metadata >= 0 ? normalized[..metadata] : normalized;
    }

    private static async Task<string?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task DelayBeforeRetry(
        CancellationToken deadlineToken,
        CancellationToken callerToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), deadlineToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // The bounded deadline elapsed.
        }
    }

    private static bool OwnsListeningPort(int expectedProcessId, int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        uint size = 0;
        var first = NativeMethods.GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            true,
            AddressFamilyIpv4,
            TcpTableOwnerPidListener,
            0);
        if (first != InsufficientBuffer || size == 0)
        {
            return false;
        }

        var table = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (NativeMethods.GetExtendedTcpTable(
                    table,
                    ref size,
                    true,
                    AddressFamilyIpv4,
                    TcpTableOwnerPidListener,
                    0) != NoError)
            {
                return false;
            }

            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPointer = IntPtr.Add(table, sizeof(uint));
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(
                    IntPtr.Add(rowPointer, index * rowSize));
                var localPort = (ushort)IPAddress.NetworkToHostOrder(
                    unchecked((short)(row.LocalPort & 0xFFFF)));
                if (row.OwningPid == expectedProcessId && localPort == port)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public int OwningPid;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("iphlpapi.dll", SetLastError = true)]
        internal static partial uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref uint size,
            [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily,
            int tableClass,
            uint reserved);
    }
}
