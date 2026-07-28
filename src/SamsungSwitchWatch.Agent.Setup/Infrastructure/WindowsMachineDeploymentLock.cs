using System.Security.AccessControl;
using System.Security.Principal;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Infrastructure;

public sealed class WindowsMachineDeploymentLock : IMachineDeploymentLock
{
    private const string LockName = @"Global\SamsungSwitchWatch.Agent.Setup.Deployment.v1";

    public IDisposable Acquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var security = new SemaphoreSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new SemaphoreAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            SemaphoreRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new SemaphoreAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            SemaphoreRights.FullControl,
            AccessControlType.Allow));

        Semaphore semaphore;
        try
        {
            semaphore = SemaphoreAcl.Create(
                1,
                1,
                LockName,
                out _,
                security);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SetupException(
                SetupErrorCodes.AlreadyRunning,
                "다른 설치 작업이 실행 중이거나 설치 잠금 권한을 확인할 수 없습니다.",
                exception);
        }

        if (!semaphore.WaitOne(TimeSpan.Zero))
        {
            semaphore.Dispose();
            throw new SetupException(
                SetupErrorCodes.AlreadyRunning,
                "다른 Agent 설치 또는 복구 작업이 실행 중입니다.");
        }

        return new SemaphoreLease(semaphore);
    }

    private sealed class SemaphoreLease(Semaphore semaphore) : IDisposable
    {
        private Semaphore? _semaphore = semaphore;

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _semaphore, null);
            if (value is null)
            {
                return;
            }

            value.Release();
            value.Dispose();
        }
    }
}
