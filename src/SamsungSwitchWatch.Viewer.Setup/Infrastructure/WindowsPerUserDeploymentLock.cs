using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Infrastructure;

public sealed class WindowsPerUserDeploymentLock : IViewerDeploymentLock
{
    public IDisposable Acquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        Semaphore semaphore;
        try
        {
            semaphore = new Semaphore(1, 1, BuildName());
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.AlreadyRunning,
                "Viewer 설치 작업 잠금을 열 수 없습니다.",
                exception);
        }

        var acquired = false;
        try
        {
            acquired = semaphore.WaitOne(TimeSpan.Zero);

            if (!acquired)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.AlreadyRunning,
                    "다른 Viewer 설치 또는 복구 작업이 실행 중입니다.");
            }

            return new SemaphoreLease(semaphore);
        }
        catch
        {
            if (!acquired)
            {
                semaphore.Dispose();
            }

            throw;
        }
    }

    private static string BuildName()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ??
                       Environment.UserName;
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $@"Local\SamsungSwitchWatch.Viewer.Setup.{digest}.v1";
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
