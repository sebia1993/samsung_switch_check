using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Infrastructure;

public sealed partial class WindowsServiceManager : IServiceManager
{
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceConfigDescription = 1;
    private const int ServiceConfigFailureActions = 2;
    private const int ServiceConfigFailureActionsFlag = 4;
    private const int ServiceConfigServiceSidInfo = 5;
    private const uint ServiceSidTypeUnrestricted = 1;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const uint DaclSecurityInformation = 0x00000004;
    private static readonly TimeSpan ServiceStatePollInterval =
        TimeSpan.FromMilliseconds(200);

    public ServiceSnapshot Capture(string serviceName)
    {
        EnsureWindows();
        var scm = OpenScManager();
        try
        {
            var service = NativeMethods.OpenService(scm, serviceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    return ServiceSnapshot.Missing;
                }

                ThrowServiceFailure();
            }

            try
            {
                var config = QueryConfig(service);
                var status = QueryStatus(service);
                return new ServiceSnapshot(
                    true,
                    status.CurrentState == ServiceRunning,
                    config.BinaryPath,
                    config.StartType,
                    config.AccountName,
                    config.DisplayName,
                    QueryDescription(service),
                    QueryServiceSidType(service),
                    QueryRecovery(service),
                    QueryServiceSecurityDescriptor(service),
                    checked((int)status.ProcessId));
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    internal static bool IsServiceRunningReadOnly(string serviceName)
    {
        EnsureWindows();
        var scm = NativeMethods.OpenSCManager(null, null, 0x00000001);
        if (scm == IntPtr.Zero)
        {
            ThrowServiceFailure();
        }

        try
        {
            var service = NativeMethods.OpenService(scm, serviceName, 0x00000004);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return QueryStatus(service).CurrentState == ServiceRunning;
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    public void Stop(string serviceName, TimeSpan timeout)
    {
        WithService(serviceName, service =>
            WaitForServiceStopAndProcessExit(service, timeout));
    }

    public void InstallOrUpdate(
        string serviceName,
        string displayName,
        string binaryPath,
        string accountName)
    {
        EnsureWindows();
        var scm = OpenScManager();
        try
        {
            var service = NativeMethods.OpenService(scm, serviceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceDoesNotExist)
                {
                    ThrowServiceFailure();
                }

                service = NativeMethods.CreateService(
                    scm,
                    serviceName,
                    displayName,
                    ServiceAllAccess,
                    ServiceWin32OwnProcess,
                    2,
                    ServiceErrorNormal,
                    binaryPath,
                    null,
                    IntPtr.Zero,
                    null,
                    accountName,
                    null);
                if (service == IntPtr.Zero)
                {
                    ThrowServiceFailure();
                }
            }
            else if (!NativeMethods.ChangeServiceConfig(
                         service,
                         ServiceNoChange,
                         2,
                         ServiceNoChange,
                         binaryPath,
                         null,
                         IntPtr.Zero,
                         null,
                         accountName,
                         null,
                         displayName))
            {
                NativeMethods.CloseServiceHandle(service);
                ThrowServiceFailure();
            }

            try
            {
                SetDescription(service, "Windowless Samsung switch Telnet execution Agent");
                SetServiceSidType(service);
                ApplyRestrictedServiceDacl(service, serviceName);
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    public void ConfigureRecovery(string serviceName)
    {
        WithService(
            serviceName,
            service => SetRecovery(service, CreateAutomaticRecoveryPolicy()));
    }

    public void DisableRecovery(string serviceName)
    {
        WithService(
            serviceName,
            service =>
            {
                SetRecovery(service, CreateDisabledRecoveryPolicy());
                var readback = QueryRecovery(service);
                if (readback.Actions.Count != 0)
                {
                    throw new SetupException(
                        SetupErrorCodes.ServiceFailed,
                        "Windows 서비스의 자동 복구 작업이 비활성화되었는지 확인하지 못했습니다.");
                }
            });
    }

    public void Start(string serviceName, TimeSpan timeout)
    {
        WithService(serviceName, service =>
        {
            if (QueryStatus(service).CurrentState == ServiceRunning)
            {
                return;
            }

            if (!NativeMethods.StartService(service, 0, null))
            {
                ThrowServiceFailure();
            }

            WaitForState(service, ServiceRunning, timeout);
        });
    }

    public void Restore(string serviceName, ServiceSnapshot snapshot)
    {
        var current = Capture(serviceName);
        if (!snapshot.Exists)
        {
            if (!current.Exists)
            {
                return;
            }

            if (current.Running)
            {
                Stop(serviceName, TimeSpan.FromSeconds(20));
            }

            WithService(serviceName, service =>
            {
                if (!NativeMethods.DeleteService(service))
                {
                    ThrowServiceFailure();
                }
            });
            return;
        }

        EnsureWindows();
        var scm = OpenScManager();
        try
        {
            var service = NativeMethods.OpenService(scm, serviceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() != ErrorServiceDoesNotExist)
                {
                    ThrowServiceFailure();
                }

                service = NativeMethods.CreateService(
                    scm,
                    serviceName,
                    snapshot.DisplayName,
                    ServiceAllAccess,
                    ServiceWin32OwnProcess,
                    snapshot.StartType,
                    ServiceErrorNormal,
                    snapshot.BinaryPath,
                    null,
                    IntPtr.Zero,
                    null,
                    snapshot.AccountName,
                    null);
                if (service == IntPtr.Zero)
                {
                    ThrowServiceFailure();
                }
            }
            else if (!NativeMethods.ChangeServiceConfig(
                         service,
                         ServiceNoChange,
                         snapshot.StartType,
                         ServiceNoChange,
                         snapshot.BinaryPath,
                         null,
                         IntPtr.Zero,
                         null,
                         snapshot.AccountName,
                         null,
                         snapshot.DisplayName))
            {
                NativeMethods.CloseServiceHandle(service);
                ThrowServiceFailure();
            }

            try
            {
                SetDescription(service, snapshot.Description);
                SetServiceSidType(service, snapshot.ServiceSidType);
                SetRecovery(service, snapshot.Recovery);
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }

        if (snapshot.Running)
        {
            Start(serviceName, TimeSpan.FromSeconds(30));
        }

        if (snapshot.SecurityDescriptor is not null)
        {
            WithService(serviceName, service =>
                ApplyServiceSecurityDescriptor(service, snapshot.SecurityDescriptor));
        }
    }

    private static IntPtr OpenScManager()
    {
        var handle = NativeMethods.OpenSCManager(null, null, ScManagerAllAccess);
        if (handle == IntPtr.Zero)
        {
            ThrowServiceFailure();
        }

        return handle;
    }

    private static void WithService(string serviceName, Action<IntPtr> action)
    {
        EnsureWindows();
        var scm = OpenScManager();
        try
        {
            var service = NativeMethods.OpenService(scm, serviceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                ThrowServiceFailure();
            }

            try
            {
                action(service);
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    private static ManagedServiceConfig QueryConfig(IntPtr service)
    {
        _ = NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        var pointer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, pointer, needed, out _))
            {
                ThrowServiceFailure();
            }

            var native = Marshal.PtrToStructure<QueryServiceConfig>(pointer);
            return new ManagedServiceConfig(
                Marshal.PtrToStringUni(native.BinaryPathName) ?? string.Empty,
                native.StartType,
                Marshal.PtrToStringUni(native.ServiceStartName) ?? string.Empty,
                Marshal.PtrToStringUni(native.DisplayName) ?? string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static ServiceStatusProcess QueryStatus(IntPtr service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.QueryServiceStatusEx(
                    service,
                    ScStatusProcessInfo,
                    pointer,
                    (uint)size,
                    out _))
            {
                ThrowServiceFailure();
            }

            return Marshal.PtrToStructure<ServiceStatusProcess>(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void WaitForState(IntPtr service, uint expectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (QueryStatus(service).CurrentState == expectedState)
            {
                return;
            }

            Thread.Sleep(200);
        }

        throw new SetupException(
            SetupErrorCodes.ServiceFailed,
            "Windows 서비스가 제한 시간 안에 요청한 상태가 되지 않았습니다.");
    }

    private static void WaitForServiceStopAndProcessExit(
        IntPtr service,
        TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        var observedProcesses = new Dictionary<int, Process>();
        var stopRequestedProcessIds = new HashSet<int>();
        try
        {
            while (true)
            {
                var status = QueryStatus(service);
                TrackProcess(status.ProcessId, observedProcesses);
                if (IsStopComplete(
                        status.CurrentState,
                        observedProcesses.Values.Select(ProcessHasExited)))
                {
                    return;
                }

                if (elapsed.Elapsed >= timeout)
                {
                    ThrowServiceStopTimeout();
                }

                var processId = checked((int)status.ProcessId);
                if (ShouldRequestStop(
                        status.CurrentState,
                        processId,
                        stopRequestedProcessIds))
                {
                    if (!NativeMethods.ControlService(
                            service,
                            ServiceControlStop,
                            out _))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceNotActive)
                        {
                            ThrowServiceFailure(error);
                        }
                    }
                }

                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    ThrowServiceStopTimeout();
                }

                Thread.Sleep(
                    remaining < ServiceStatePollInterval
                        ? remaining
                        : ServiceStatePollInterval);
            }
        }
        finally
        {
            foreach (var process in observedProcesses.Values)
            {
                process.Dispose();
            }
        }
    }

    internal static bool IsStopComplete(
        uint currentState,
        IEnumerable<bool> observedProcessExitStates) =>
        currentState == ServiceStopped &&
        observedProcessExitStates.All(hasExited => hasExited);

    internal static bool ShouldRequestStop(
        uint currentState,
        int processId,
        ISet<int> stopRequestedProcessIds)
    {
        if (currentState is
                ServiceStopped or
                ServiceStartPending or
                ServiceStopPending)
        {
            return false;
        }

        return stopRequestedProcessIds.Add(processId);
    }

    internal static ServiceRecoverySnapshot CreateAutomaticRecoveryPolicy() =>
        new(
            86400,
            true,
            string.Empty,
            string.Empty,
            [
                new ServiceFailureActionSnapshot(1, 5000),
                new ServiceFailureActionSnapshot(1, 15000),
                new ServiceFailureActionSnapshot(1, 60000)
            ]);

    internal static ServiceRecoverySnapshot CreateDisabledRecoveryPolicy() =>
        new(
            0,
            false,
            string.Empty,
            string.Empty,
            []);

    private static void TrackProcess(
        uint processId,
        IDictionary<int, Process> observedProcesses)
    {
        if (processId == 0)
        {
            return;
        }

        var checkedProcessId = checked((int)processId);
        if (observedProcesses.ContainsKey(checkedProcessId))
        {
            return;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(checkedProcessId);
            _ = process.Handle;
            observedProcesses.Add(checkedProcessId, process);
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            // The process exited between the SCM status query and handle open.
        }
        catch (InvalidOperationException)
        {
            process?.Dispose();
            // The process exited between lookup and handle acquisition.
        }
        catch (Win32Exception exception)
        {
            process?.Dispose();
            ThrowServiceFailure(exception.NativeErrorCode);
        }
    }

    private static bool ProcessHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception exception)
        {
            ThrowServiceFailure(exception.NativeErrorCode);
            return false;
        }
    }

    private static void ThrowServiceStopTimeout() =>
        throw new SetupException(
            SetupErrorCodes.ServiceFailed,
            "Windows 서비스가 제한 시간 안에 종료되어 프로그램 파일을 해제하지 못했습니다.");

    private static void SetDescription(IntPtr service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description ?? string.Empty);
        try
        {
            var value = new ServiceDescription { Description = text };
            if (!NativeMethods.ChangeServiceConfig2(
                    service,
                    ServiceConfigDescription,
                    ref value))
            {
                ThrowServiceFailure();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void SetServiceSidType(
        IntPtr service,
        uint serviceSidType = ServiceSidTypeUnrestricted)
    {
        var value = new ServiceSidInfo { ServiceSidType = serviceSidType };
        if (!NativeMethods.ChangeServiceConfig2(
                service,
                ServiceConfigServiceSidInfo,
                ref value))
        {
            ThrowServiceFailure();
        }
    }

    private static string QueryDescription(IntPtr service)
    {
        var pointer = QueryConfig2Buffer(service, ServiceConfigDescription);
        try
        {
            var value = Marshal.PtrToStructure<ServiceDescription>(pointer);
            return Marshal.PtrToStringUni(value.Description) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static uint QueryServiceSidType(IntPtr service)
    {
        var pointer = QueryConfig2Buffer(service, ServiceConfigServiceSidInfo);
        try
        {
            return Marshal.PtrToStructure<ServiceSidInfo>(pointer).ServiceSidType;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static ServiceRecoverySnapshot QueryRecovery(IntPtr service)
    {
        var pointer = QueryConfig2Buffer(service, ServiceConfigFailureActions);
        try
        {
            var value = Marshal.PtrToStructure<ServiceFailureActions>(pointer);
            if (value.ActionsCount > 64)
            {
                throw new SetupException(
                    SetupErrorCodes.ServiceFailed,
                    "Windows 서비스 복구 정책의 작업 수가 안전 제한을 초과했습니다.");
            }

            var actions = new List<ServiceFailureActionSnapshot>(
                checked((int)value.ActionsCount));
            var actionSize = Marshal.SizeOf<ScAction>();
            for (var index = 0; index < value.ActionsCount; index++)
            {
                var action = Marshal.PtrToStructure<ScAction>(
                    IntPtr.Add(value.Actions, checked((int)index * actionSize)));
                actions.Add(new ServiceFailureActionSnapshot(action.Type, action.Delay));
            }

            var flagPointer = QueryConfig2Buffer(
                service,
                ServiceConfigFailureActionsFlag);
            try
            {
                var applyOnNonCrashFailures = Marshal.ReadInt32(flagPointer) != 0;
                return new ServiceRecoverySnapshot(
                    value.ResetPeriod,
                    applyOnNonCrashFailures,
                    Marshal.PtrToStringUni(value.RebootMessage) ?? string.Empty,
                    Marshal.PtrToStringUni(value.Command) ?? string.Empty,
                    actions);
            }
            finally
            {
                Marshal.FreeHGlobal(flagPointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static IntPtr QueryConfig2Buffer(IntPtr service, int infoLevel)
    {
        _ = NativeMethods.QueryServiceConfig2(
            service,
            infoLevel,
            IntPtr.Zero,
            0,
            out var needed);
        if (needed == 0 || needed > 1024 * 1024)
        {
            ThrowServiceFailure();
        }

        var pointer = Marshal.AllocHGlobal(checked((int)needed));
        if (!NativeMethods.QueryServiceConfig2(
                service,
                infoLevel,
                pointer,
                needed,
                out _))
        {
            Marshal.FreeHGlobal(pointer);
            ThrowServiceFailure();
        }

        return pointer;
    }

    private static void SetRecovery(
        IntPtr service,
        ServiceRecoverySnapshot recovery)
    {
        var actions = recovery.Actions ?? [];
        if (actions.Count > 64)
        {
            throw new SetupException(
                SetupErrorCodes.ServiceFailed,
                "Windows 서비스 복구 정책의 작업 수가 안전 제한을 초과했습니다.");
        }

        var actionSize = Marshal.SizeOf<ScAction>();
        var actionsPointer = IntPtr.Zero;
        var rebootMessagePointer = IntPtr.Zero;
        var commandPointer = IntPtr.Zero;
        try
        {
            // SERVICE_FAILURE_ACTIONS only deletes an existing action array
            // when cActions is zero and lpsaActions is non-null. A null
            // pointer would leave the old restart actions unchanged.
            actionsPointer = Marshal.AllocHGlobal(
                GetRecoveryActionsAllocationSize(actions.Count));
            rebootMessagePointer = string.IsNullOrEmpty(recovery.RebootMessage)
                ? IntPtr.Zero
                : Marshal.StringToHGlobalUni(recovery.RebootMessage);
            commandPointer = string.IsNullOrEmpty(recovery.Command)
                ? IntPtr.Zero
                : Marshal.StringToHGlobalUni(recovery.Command);

            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                Marshal.StructureToPtr(
                    new ScAction { Type = action.Type, Delay = action.Delay },
                    IntPtr.Add(actionsPointer, checked(index * actionSize)),
                    false);
            }

            var failureActions = new ServiceFailureActions
            {
                ResetPeriod = recovery.ResetPeriod,
                RebootMessage = rebootMessagePointer,
                Command = commandPointer,
                ActionsCount = checked((uint)actions.Count),
                Actions = actionsPointer
            };
            if (!NativeMethods.ChangeServiceConfig2(
                    service,
                    ServiceConfigFailureActions,
                    ref failureActions))
            {
                ThrowServiceFailure();
            }

            var enabled = recovery.ApplyOnNonCrashFailures ? 1 : 0;
            if (!NativeMethods.ChangeServiceConfig2(
                    service,
                    ServiceConfigFailureActionsFlag,
                    ref enabled))
            {
                ThrowServiceFailure();
            }
        }
        finally
        {
            if (actionsPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(actionsPointer);
            }

            if (rebootMessagePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(rebootMessagePointer);
            }

            if (commandPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(commandPointer);
            }
        }
    }

    private static byte[] QueryServiceSecurityDescriptor(IntPtr service)
    {
        _ = NativeMethods.QueryServiceObjectSecurity(
            service,
            DaclSecurityInformation,
            null,
            0,
            out var required);
        if (required == 0)
        {
            ThrowServiceFailure();
        }

        var descriptor = new byte[required];
        if (!NativeMethods.QueryServiceObjectSecurity(
                service,
                DaclSecurityInformation,
                descriptor,
                required,
                out _))
        {
            ThrowServiceFailure();
        }

        return descriptor;
    }

    private static void ApplyRestrictedServiceDacl(IntPtr service, string serviceName)
    {
        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName)
            .Translate(typeof(SecurityIdentifier));

        var dacl = new RawAcl(GenericAcl.AclRevision, 3);
        dacl.InsertAce(
            dacl.Count,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                unchecked((int)ServiceAllAccess),
                system,
                isCallback: false,
                opaque: null));
        dacl.InsertAce(
            dacl.Count,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                unchecked((int)ServiceAllAccess),
                administrators,
                isCallback: false,
                opaque: null));
        dacl.InsertAce(
            dacl.Count,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                0x00000004,
                serviceSid,
                isCallback: false,
                opaque: null));

        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
            system,
            administrators,
            systemAcl: null,
            discretionaryAcl: dacl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        ApplyServiceSecurityDescriptor(service, bytes);

        var applied = new RawSecurityDescriptor(QueryServiceSecurityDescriptor(service), 0);
        if (applied.DiscretionaryAcl is null ||
            GrantsStopToUnexpectedPrincipal(
                applied.DiscretionaryAcl,
                administrators,
                system,
                serviceSid))
        {
            throw new SetupException(
                SetupErrorCodes.ServiceFailed,
                "Agent 서비스 중지 권한 제한을 확인하지 못했습니다.");
        }
    }

    internal static bool GrantsStopToUnexpectedPrincipal(
        RawAcl dacl,
        SecurityIdentifier administrators,
        SecurityIdentifier system,
        SecurityIdentifier serviceSid)
    {
        const int serviceStop = 0x00000020;
        const int genericAll = 0x10000000;
        const int genericExecute = 0x20000000;
        foreach (GenericAce ace in dacl)
        {
            if (ace is not QualifiedAce
                {
                    AceQualifier: AceQualifier.AccessAllowed
                } qualified ||
                qualified.SecurityIdentifier is null ||
                (qualified.AccessMask & (serviceStop | genericAll | genericExecute)) == 0)
            {
                continue;
            }

            if (qualified.SecurityIdentifier != administrators &&
                qualified.SecurityIdentifier != system &&
                qualified.SecurityIdentifier != serviceSid)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyServiceSecurityDescriptor(IntPtr service, byte[] descriptor)
    {
        if (!NativeMethods.SetServiceObjectSecurity(
                service,
                DaclSecurityInformation,
                descriptor))
        {
            ThrowServiceFailure();
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
    }

    private static void ThrowServiceFailure()
    {
        var error = Marshal.GetLastWin32Error();
        ThrowServiceFailure(error);
    }

    internal static int GetRecoveryActionsAllocationSize(int actionCount)
    {
        if (actionCount is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(actionCount));
        }

        var actionSize = Marshal.SizeOf<ScAction>();
        return actionCount == 0
            ? actionSize
            : checked(actionSize * actionCount);
    }

    private static void ThrowServiceFailure(int error)
    {
        throw new SetupException(
            SetupErrorCodes.ServiceFailed,
            $"Windows 서비스 작업에 실패했습니다. 오류 코드: {error}",
            new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    private sealed record ManagedServiceConfig(
        string BinaryPath,
        uint StartType,
        string AccountName,
        string DisplayName);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceSidInfo
    {
        public uint ServiceSidType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint ActionsCount;
        public IntPtr Actions;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW",
            SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr OpenSCManager(
            string? machineName,
            string? databaseName,
            uint desiredAccess);

        [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW",
            SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr OpenService(
            IntPtr serviceManager,
            string serviceName,
            uint desiredAccess);

        [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW",
            SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr CreateService(
            IntPtr serviceManager,
            string serviceName,
            string displayName,
            uint desiredAccess,
            uint serviceType,
            uint startType,
            uint errorControl,
            string binaryPath,
            string? loadOrderGroup,
            IntPtr tagId,
            string? dependencies,
            string accountName,
            string? password);

        [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW",
            SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ChangeServiceConfig(
            IntPtr service,
            uint serviceType,
            uint startType,
            uint errorControl,
            string? binaryPath,
            string? loadOrderGroup,
            IntPtr tagId,
            string? dependencies,
            string? accountName,
            string? password,
            string? displayName);

        [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryServiceConfig(
            IntPtr service,
            IntPtr serviceConfig,
            uint bufferSize,
            out uint bytesNeeded);

        [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryServiceConfig2(
            IntPtr service,
            int infoLevel,
            IntPtr buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryServiceStatusEx(
            IntPtr service,
            int infoLevel,
            IntPtr buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ControlService(
            IntPtr service,
            uint control,
            out ServiceStatus status);

        [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW",
            SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool StartService(
            IntPtr service,
            int argumentCount,
            string[]? arguments);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteService(IntPtr service);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseServiceHandle(IntPtr service);

        [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ChangeServiceConfig2(
            IntPtr service,
            int infoLevel,
            ref ServiceDescription info);

        [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ChangeServiceConfig2(
            IntPtr service,
            int infoLevel,
            ref ServiceFailureActions info);

        [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ChangeServiceConfig2(
            IntPtr service,
            int infoLevel,
            ref ServiceSidInfo info);

        [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ChangeServiceConfig2(
            IntPtr service,
            int infoLevel,
            ref int info);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryServiceObjectSecurity(
            IntPtr service,
            uint securityInformation,
            [Out] byte[]? securityDescriptor,
            uint bufferSize,
            out uint bytesNeeded);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetServiceObjectSecurity(
            IntPtr service,
            uint securityInformation,
            byte[] securityDescriptor);
    }
}
