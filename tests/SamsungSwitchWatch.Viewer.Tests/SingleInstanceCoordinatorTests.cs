using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Theory]
    [InlineData("0.10.0-poc+abcdef", "0.10.0-poc")]
    [InlineData(" 0.10.0_poc ", "0.10.0_poc")]
    [InlineData("", "unknown")]
    [InlineData("0.10.0/poc", "0.10.0_poc")]
    public void BuildVersionMutexToken_IsStableAndSafe(string version, string expected)
    {
        Assert.Equal(expected, SingleInstanceCoordinator.BuildVersionMutexToken(version));
    }

    [Fact]
    public async Task LegacySharedMutexOnly_IsReportedAsDifferentVersion_ThenCanAcquire()
    {
        var names = Names();
        using var ownerReady = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var legacy = new Mutex(false, names.Shared);
            legacy.WaitOne();
            ownerReady.Set();
            releaseOwner.Wait();
            legacy.ReleaseMutex();
        });
        owner.IsBackground = true;
        owner.Start();
        Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(5)));

        await using (var blocked = new SingleInstanceCoordinator(
                         names.Shared,
                         names.Version,
                         names.Pipe))
        {
            Assert.Equal(
                SingleInstanceAcquireResult.DifferentVersionRunning,
                blocked.TryAcquire());
        }

        releaseOwner.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));

        await using var acquired = new SingleInstanceCoordinator(
            names.Shared,
            names.Version,
            names.Pipe);
        Assert.Equal(SingleInstanceAcquireResult.Acquired, acquired.TryAcquire());
    }

    [Fact]
    public async Task CurrentVersionMutex_IsReportedAndActivationReachesOwner()
    {
        var names = Names();
        using var ownerReady = new ManualResetEventSlim();
        using var activated = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        Exception? ownerFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                var coordinator = new SingleInstanceCoordinator(
                    names.Shared,
                    names.Version,
                    names.Pipe);
                if (coordinator.TryAcquire() != SingleInstanceAcquireResult.Acquired)
                {
                    throw new InvalidOperationException("Owner did not acquire test mutexes.");
                }
                coordinator.ActivationRequested += (_, _) => activated.Set();
                ownerReady.Set();
                releaseOwner.Wait();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                ownerFailure = exception;
                ownerReady.Set();
            }
        });
        owner.IsBackground = true;
        owner.Start();
        Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(ownerFailure);

        await using (var blocked = new SingleInstanceCoordinator(
                         names.Shared,
                         names.Version,
                         names.Pipe))
        {
            Assert.Equal(
                SingleInstanceAcquireResult.CurrentVersionAlreadyRunning,
                blocked.TryAcquire());
        }

        await SingleInstanceCoordinator.NotifyExistingAsync(
            pipeName: names.Pipe);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));

        releaseOwner.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(ownerFailure);
    }

    private static (string Shared, string Version, string Pipe) Names()
    {
        var id = Guid.NewGuid().ToString("N");
        return (
            $@"Local\SamsungSwitchWatch.Viewer.Tests.Shared.{id}",
            $@"Local\SamsungSwitchWatch.Viewer.Tests.Version.{id}",
            $"SamsungSwitchWatch.Viewer.Tests.Pipe.{id}");
    }
}
