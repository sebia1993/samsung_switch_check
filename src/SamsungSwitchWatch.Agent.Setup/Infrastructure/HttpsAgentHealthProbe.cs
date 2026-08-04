using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Infrastructure;

public sealed partial class HttpsAgentHealthProbe : IAgentHealthProbe
{
    private const int MaximumReadinessBytes = 16 * 1024;
    private const int AddressFamilyIpv4 = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Func<int, int, ListenerOwnership> _listenerOwnership;
    private readonly TimeSpan _retryDelay;

    public HttpsAgentHealthProbe()
        : this(
            CreateHandler,
            GetListenerOwnership,
            TimeSpan.FromMilliseconds(500))
    {
    }

    internal HttpsAgentHealthProbe(Func<HttpMessageHandler> handlerFactory)
        : this(
            handlerFactory,
            GetListenerOwnership,
            TimeSpan.FromMilliseconds(500))
    {
    }

    internal HttpsAgentHealthProbe(
        Func<HttpMessageHandler> handlerFactory,
        Func<int, int, ListenerOwnership> listenerOwnership,
        TimeSpan retryDelay)
    {
        _handlerFactory = handlerFactory ??
                          throw new ArgumentNullException(nameof(handlerFactory));
        _listenerOwnership = listenerOwnership ??
                             throw new ArgumentNullException(nameof(listenerOwnership));
        if (retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        _retryDelay = retryDelay;
    }

    public async Task<AgentHealthProbeResult> WaitUntilReadyAsync(
        Uri endpoint,
        string? expectedProductVersion,
        Func<ServiceSnapshot> currentServiceSnapshot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(currentServiceSnapshot);
        if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The native Setup health probe is restricted to loopback HTTPS.",
                nameof(endpoint));
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var handler = _handlerFactory();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var lastCode = AgentHealthProbeCode.DeadlineExceeded;
        var firstProcessId = 0;
        var restartObserved = false;
        var serviceRunningObserved = false;
        var listenerOwnedObserved = false;
        var httpAttemptCount = 0;
        var lastTransportPhase = AgentHealthTransportPhase.NotStarted;

        while (!deadline.IsCancellationRequested)
        {
            ServiceSnapshot service;
            try
            {
                service = currentServiceSnapshot();
            }
            catch
            {
                lastCode = AgentHealthProbeCode.ServiceInspectionFailed;
                await DelayBeforeRetry(
                    _retryDelay,
                    deadline.Token,
                    cancellationToken);
                continue;
            }

            if (!service.Exists || !service.Running || service.ProcessId <= 0)
            {
                lastCode = AgentHealthProbeCode.ServiceUnavailable;
                await DelayBeforeRetry(
                    _retryDelay,
                    deadline.Token,
                    cancellationToken);
                continue;
            }

            serviceRunningObserved = true;

            if (firstProcessId == 0)
            {
                firstProcessId = service.ProcessId;
            }
            else if (service.ProcessId != firstProcessId)
            {
                restartObserved = true;
            }

            var ownership = _listenerOwnership(service.ProcessId, endpoint.Port);
            if (ownership != ListenerOwnership.OwnedByExpectedProcess)
            {
                lastCode = ownership switch
                {
                    ListenerOwnership.NotListening =>
                        AgentHealthProbeCode.TcpNotListening,
                    ListenerOwnership.OwnedByOtherProcess =>
                        AgentHealthProbeCode.TcpOwnedByOtherProcess,
                    _ => AgentHealthProbeCode.TcpOwnershipQueryFailed
                };
                await DelayBeforeRetry(
                    _retryDelay,
                    deadline.Token,
                    cancellationToken);
                continue;
            }

            listenerOwnedObserved = true;
            lastTransportPhase = AgentHealthTransportPhase.ListenerOwned;

            try
            {
                httpAttemptCount++;
                lastTransportPhase = AgentHealthTransportPhase.RequestStarted;
                using var readyRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var readyResponse = await client.SendAsync(
                    readyRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token);
                lastTransportPhase = AgentHealthTransportPhase.ResponseHeaders;
                if (readyResponse.StatusCode != HttpStatusCode.OK)
                {
                    lastCode = AgentHealthProbeCode.HttpStatusInvalid;
                    await DelayBeforeRetry(
                        _retryDelay,
                        deadline.Token,
                        cancellationToken);
                    continue;
                }

                if (readyResponse.Content.Headers.ContentLength >
                    MaximumReadinessBytes)
                {
                    lastCode = AgentHealthProbeCode.PayloadTooLarge;
                    await DelayBeforeRetry(
                        _retryDelay,
                        deadline.Token,
                        cancellationToken);
                    continue;
                }

                lastTransportPhase = AgentHealthTransportPhase.ResponseBody;
                var readinessJson = await ReadBoundedAsync(
                    readyResponse.Content,
                    MaximumReadinessBytes,
                    deadline.Token);
                if (readinessJson is null)
                {
                    lastCode = AgentHealthProbeCode.PayloadTooLarge;
                }
                else
                {
                    lastCode = ClassifyReadiness(
                        readinessJson,
                        expectedProductVersion);
                    lastTransportPhase =
                        AgentHealthTransportPhase.ReadinessValidated;
                    if (lastCode == AgentHealthProbeCode.Ready)
                    {
                        return AgentHealthProbeResult.Success(
                            restartObserved,
                            serviceRunningObserved,
                            listenerOwnedObserved,
                            httpAttemptCount,
                            lastTransportPhase);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (deadline.IsCancellationRequested)
                {
                    // Preserve the last completed HTTP outcome when the overall
                    // readiness deadline interrupts a later retry. If the first
                    // request itself consumed the entire deadline, the bounded
                    // transport outcome is a request timeout.
                    if (httpAttemptCount == 1)
                    {
                        lastCode = AgentHealthProbeCode.HttpsRequestTimeout;
                    }
                    break;
                }

                lastCode = AgentHealthProbeCode.HttpsRequestTimeout;
            }
            catch (HttpRequestException exception)
            {
                lastCode = ClassifyTransportFailure(exception);
            }
            catch (HttpIOException exception)
            {
                lastCode = ClassifyTransportFailure(exception);
            }
            catch (AuthenticationException)
            {
                lastCode = AgentHealthProbeCode.HttpsTlsFailed;
            }
            catch (SocketException exception)
            {
                lastCode = ClassifySocketFailure(exception);
            }
            catch (EndOfStreamException)
            {
                lastCode = AgentHealthProbeCode.HttpsEof;
            }
            catch (TimeoutException)
            {
                lastCode = AgentHealthProbeCode.HttpsRequestTimeout;
            }
            catch (IOException exception)
            {
                lastCode = ClassifyTransportFailure(
                    HttpRequestError.Unknown,
                    exception);
            }
            catch (JsonException)
            {
                lastCode = AgentHealthProbeCode.PayloadInvalid;
            }

            await DelayBeforeRetry(
                _retryDelay,
                deadline.Token,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AgentHealthProbeResult.Failure(
            lastCode,
            restartObserved,
            serviceRunningObserved,
            listenerOwnedObserved,
            httpAttemptCount,
            lastTransportPhase);
    }

    internal static AgentHealthProbeCode ClassifyTransportFailure(
        HttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ClassifyTransportFailure(
            exception.HttpRequestError,
            exception);
    }

    internal static AgentHealthProbeCode ClassifyTransportFailure(
        HttpIOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ClassifyTransportFailure(
            exception.HttpRequestError,
            exception);
    }

    private static AgentHealthProbeCode ClassifyTransportFailure(
        HttpRequestError requestError,
        Exception exception)
    {
        if (requestError == HttpRequestError.SecureConnectionError ||
            ContainsException<AuthenticationException>(exception))
        {
            return AgentHealthProbeCode.HttpsTlsFailed;
        }

        if (FindException<SocketException>(exception) is { } socketException)
        {
            return ClassifySocketFailure(socketException);
        }

        if (requestError == HttpRequestError.ResponseEnded ||
            ContainsException<EndOfStreamException>(exception))
        {
            return AgentHealthProbeCode.HttpsEof;
        }

        return requestError switch
        {
            HttpRequestError.ConnectionError or
            HttpRequestError.NameResolutionError =>
                AgentHealthProbeCode.HttpsConnectFailed,
            _ => AgentHealthProbeCode.HttpsRequestFailed
        };
    }

    private static AgentHealthProbeCode ClassifySocketFailure(
        SocketException exception) =>
        exception.SocketErrorCode == SocketError.TimedOut
            ? AgentHealthProbeCode.HttpsRequestTimeout
            : exception.SocketErrorCode is
            SocketError.ConnectionAborted or
            SocketError.ConnectionReset or
            SocketError.NetworkReset or
            SocketError.Shutdown
            ? AgentHealthProbeCode.HttpsConnectionReset
            : AgentHealthProbeCode.HttpsConnectFailed;

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception =>
        FindException<TException>(exception) is not null;

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is TException typed)
            {
                return typed;
            }
        }

        return null;
    }

    internal static bool IsExpectedReadiness(string json, string expectedProductVersion)
        => ClassifyReadiness(json, expectedProductVersion) ==
           AgentHealthProbeCode.Ready;

    internal static AgentHealthProbeCode ClassifyReadiness(
        string json,
        string? expectedProductVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            !string.Equals(status.GetString(), "ready", StringComparison.Ordinal) ||
            !root.TryGetProperty("apiVersion", out var apiVersion) ||
            apiVersion.ValueKind != JsonValueKind.Number ||
            !apiVersion.TryGetInt32(out var api))
        {
            return AgentHealthProbeCode.PayloadInvalid;
        }

        var hasProtocol = root.TryGetProperty("protocol", out var protocol);
        var hasProductVersion =
            root.TryGetProperty("productVersion", out var version);
        if ((hasProtocol && protocol.ValueKind != JsonValueKind.String) ||
            (hasProductVersion && version.ValueKind != JsonValueKind.String) ||
            (expectedProductVersion is not null &&
             (!hasProtocol || !hasProductVersion)))
        {
            return AgentHealthProbeCode.PayloadInvalid;
        }

        if (api != 4)
        {
            return AgentHealthProbeCode.ApiVersionMismatch;
        }

        if (hasProtocol &&
            !string.Equals(
                protocol.GetString(),
                "https",
                StringComparison.Ordinal))
        {
            return AgentHealthProbeCode.ProtocolMismatch;
        }

        var actualVersion = hasProductVersion
            ? NormalizeVersion(version.GetString())
            : string.Empty;
        if (hasProductVersion && actualVersion.Length == 0)
        {
            return AgentHealthProbeCode.PayloadInvalid;
        }

        if (expectedProductVersion is null)
        {
            return AgentHealthProbeCode.Ready;
        }

        return string.Equals(
            actualVersion,
            NormalizeVersion(expectedProductVersion),
            StringComparison.OrdinalIgnoreCase)
            ? AgentHealthProbeCode.Ready
            : AgentHealthProbeCode.ProductVersionMismatch;
    }

    private static HttpMessageHandler CreateHandler() =>
        new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

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
        TimeSpan retryDelay,
        CancellationToken deadlineToken,
        CancellationToken callerToken)
    {
        try
        {
            await Task.Delay(retryDelay, deadlineToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // The bounded deadline elapsed.
        }
    }

    private static ListenerOwnership GetListenerOwnership(
        int expectedProcessId,
        int port)
    {
        if (!OperatingSystem.IsWindows() || expectedProcessId <= 0)
        {
            return ListenerOwnership.QueryFailed;
        }

        uint size = 0;
        var first = NativeMethods.GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            true,
            AddressFamilyIpv4,
            TcpTableOwnerPidListener,
            0);
        if (first == NoError && size == 0)
        {
            return ListenerOwnership.NotListening;
        }
        if (first != InsufficientBuffer || size == 0)
        {
            return ListenerOwnership.QueryFailed;
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
                return ListenerOwnership.QueryFailed;
            }

            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPointer = IntPtr.Add(table, sizeof(uint));
            var matchingPortFound = false;
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(
                    IntPtr.Add(rowPointer, index * rowSize));
                var localPort = (ushort)IPAddress.NetworkToHostOrder(
                    unchecked((short)(row.LocalPort & 0xFFFF)));
                if (row.OwningPid == expectedProcessId && localPort == port)
                {
                    return ListenerOwnership.OwnedByExpectedProcess;
                }
                if (localPort == port)
                {
                    matchingPortFound = true;
                }
            }

            return matchingPortFound
                ? ListenerOwnership.OwnedByOtherProcess
                : ListenerOwnership.NotListening;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    internal enum ListenerOwnership : byte
    {
        OwnedByExpectedProcess = 0,
        NotListening = 1,
        OwnedByOtherProcess = 2,
        QueryFailed = 3
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
