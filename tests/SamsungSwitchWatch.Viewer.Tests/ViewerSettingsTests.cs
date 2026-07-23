using System.IO;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerSettingsTests
{
    [Fact]
    public void Sanitize_MigratesLegacyHttpsAndRemovesUnsafeUriParts()
    {
        var source = new ViewerSettings
        {
            DemoMode = false,
            AgentUri = "https://user:secret@monitor.example.test:18443/path?value=leak#part",
            LastEventSequence = -50,
            MainWidth = 30,
            MainHeight = double.PositiveInfinity
        };

        var clean = ViewerSettingsSanitizer.Sanitize(source);

        Assert.Equal("https://monitor.example.test:18443", clean.AgentUri);
        Assert.Equal(0, clean.LastEventSequence);
        Assert.Equal(1280, clean.MainWidth);
        Assert.Equal(900, clean.MainHeight);
        Assert.DoesNotContain("secret", clean.AgentUri, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://monitor.example.test:18443", "https://monitor.example.test:18443")]
    [InlineData("https://192.0.2.10:18443", "https://192.0.2.10:18443")]
    [InlineData("https://monitor.example.test", "https://monitor.example.test:18443")]
    public void Sanitize_MigratesLegacyHttpToHttps(string input, string expected)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { AgentUri = input });

        Assert.Equal(expected, clean.AgentUri);
        Assert.True(ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out var reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("file:///c:/agent")]
    [InlineData("not a uri")]
    [InlineData("http://monitor.example.test:0")]
    [InlineData("http://[::1]:18443")]
    public void Sanitize_RejectsUnsupportedAgentUris(string input)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { AgentUri = input });

        Assert.Empty(clean.AgentUri);
        Assert.False(ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out _));
    }

    [Theory]
    [InlineData("10.10.10.20", "18443", "https://10.10.10.20:18443")]
    [InlineData("monitor-pc.corp.local", "18443", "https://monitor-pc.corp.local:18443")]
    public void ConnectionInput_AcceptsIpv4OrDnsAndPort(string address, string port, string expected)
    {
        Assert.True(ViewerSettingsSanitizer.TryBuildAgentUri(address, port, out var uri, out var reason));
        Assert.Equal(expected, uri);
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("http://monitor", "18443")]
    [InlineData("::1", "18443")]
    [InlineData("monitor pc", "18443")]
    [InlineData("monitor", "0")]
    [InlineData("monitor", "65536")]
    [InlineData("monitor", "443")]
    [InlineData("monitor", "not-a-port")]
    public void ConnectionInput_RejectsUnsupportedValues(string address, string port) =>
        Assert.False(ViewerSettingsSanitizer.TryBuildAgentUri(address, port, out _, out _));

    [Fact]
    public void EventCursor_IsScopedByAgentUriAndAgentId()
    {
        var settings = new ViewerSettings { AgentUri = "http://agent-a.example.test:18443" };
        settings.SetEventCursor("agent-1", 42);

        Assert.True(settings.TryGetEventCursor("agent-1", out var cursor));
        Assert.Equal(42, cursor);
        Assert.False(settings.TryGetEventCursor("agent-2", out _));

        settings.AgentUri = "http://agent-b.example.test:18443";
        Assert.False(settings.TryGetEventCursor("agent-1", out _));
    }

    [Fact]
    public void Store_MigratesLegacyConnectionDropsSecurityFieldsAndPreservesViewerState()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-SettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "viewer-settings.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, """
            {
              "DemoMode": false,
              "AgentUri": "https://agent.example.test:18443",
              "CertificateFingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "CertificateFingerprints": ["BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"],
              "ProtectedBearerToken": "dpapi:not-base64",
              "EventCursors": {"LEGACY-PIN-IDENTITY": 77},
              "MiniTopmost": false,
              "MainWidth": 1600,
              "MainHeight": 920,
              "StartMinimizedToTray": true
            }
            """);
            var store = new ViewerSettingsStore(path);

            var loaded = store.Load();

            Assert.Equal(ViewerSettingsLoadStatus.Ok, store.LastLoadStatus);
            Assert.False(loaded.DemoMode);
            Assert.Equal("https://agent.example.test:18443", loaded.AgentUri);
            Assert.False(loaded.MiniTopmost);
            Assert.Equal(1600, loaded.MainWidth);
            Assert.Equal(920, loaded.MainHeight);
            Assert.True(loaded.StartMinimizedToTray);
            Assert.Equal(77, loaded.EventCursors["LEGACY-PIN-IDENTITY"]);

            var migratedJson = File.ReadAllText(path);
            Assert.DoesNotContain("http://", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Certificate", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fingerprint", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Token", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LEGACY-PIN-IDENTITY", migratedJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Store_CorruptJsonIsQuarantinedAndReturnsSafeDefaults()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-SettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "viewer-settings.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "{ definitely-not-json");
            var store = new ViewerSettingsStore(path);

            var loaded = store.Load();

            Assert.False(loaded.DemoMode);
            Assert.Equal(ViewerSettingsLoadStatus.Corrupt, store.LastLoadStatus);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "viewer-settings.json.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"not-an-object\"")]
    public void Store_InvalidRootIsQuarantinedInsteadOfBeingReportedAsNormal(string content)
    {
        var persistence = new TestSettingsPersistence { Content = content };
        var store = new ViewerSettingsStore("viewer-settings.json", persistence);

        var loaded = store.Load();

        Assert.False(loaded.DemoMode);
        Assert.Equal(ViewerSettingsLoadStatus.Corrupt, store.LastLoadStatus);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Null(persistence.Content);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Store_ReadFailurePreservesOriginalAndDoesNotQuarantine(Type exceptionType)
    {
        const string original = """{"DemoMode":true}""";
        var persistence = new TestSettingsPersistence
        {
            Content = original,
            ReadException = (Exception)Activator.CreateInstance(exceptionType, "simulated storage failure")!
        };
        var store = new ViewerSettingsStore("viewer-settings.json", persistence);

        var loaded = store.Load();

        Assert.False(loaded.DemoMode);
        Assert.Equal(ViewerSettingsLoadStatus.StorageUnavailable, store.LastLoadStatus);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(original, persistence.Content);
    }

    [Fact]
    public void Store_MigrationWriteFailureReturnsValidatedSettingsButReportsStorageUnavailable()
    {
        const string original = """{"AgentUri":"http://monitor.example.test:18443"}""";
        var persistence = new TestSettingsPersistence
        {
            Content = original,
            WriteException = new IOException("simulated atomic write failure")
        };
        var store = new ViewerSettingsStore("viewer-settings.json", persistence);

        var loaded = store.Load();

        Assert.Equal("https://monitor.example.test:18443", loaded.AgentUri);
        Assert.Equal(ViewerSettingsLoadStatus.StorageUnavailable, store.LastLoadStatus);
        Assert.Equal(1, persistence.WriteCount);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(original, persistence.Content);
    }

    [Fact]
    public void Store_SaveFailureDoesNotReplacePreviouslyPersistedSettings()
    {
        var persistence = new TestSettingsPersistence();
        var store = new ViewerSettingsStore("viewer-settings.json", persistence);
        store.Save(new ViewerSettings { AgentUri = "https://first.example.test:18443" });
        var previous = persistence.Content;
        persistence.WriteException = new IOException("simulated atomic write failure");

        Assert.Throws<IOException>(() =>
            store.Save(new ViewerSettings { AgentUri = "https://second.example.test:18443" }));

        Assert.Equal(previous, persistence.Content);
        persistence.WriteException = null;
        Assert.Equal("https://first.example.test:18443", store.Load().AgentUri);
    }

    [Fact]
    public void SaveCoordinator_TrySaveReportsStableCodeWithoutExceptionPayload()
    {
        var diagnostics = new List<(string Stage, string Code)>();
        var persistence = new TestSettingsPersistence
        {
            WriteException = new IOException(
                "host=192.0.2.10 user=operator password=login-secret")
        };
        var coordinator = new ViewerSettingsSaveCoordinator(
            new ViewerSettingsStore("viewer-settings.json", persistence),
            (stage, code) => diagnostics.Add((stage, code)));

        var saved = coordinator.TrySave(
            new ViewerSettings { AgentUri = "https://agent.example.test:18443" },
            "settings-save-background",
            out var errorCode);

        Assert.False(saved);
        Assert.Equal("VIEWER_SETTINGS_WRITE_FAILED", errorCode);
        Assert.Equal(
            ("settings-save-background", "VIEWER_SETTINGS_WRITE_FAILED"),
            Assert.Single(diagnostics));
        Assert.DoesNotContain(
            diagnostics,
            item => item.Stage.Contains("192.0.2.10", StringComparison.Ordinal)
                    || item.Code.Contains("login-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveCoordinator_UnexpectedFailureUsesUnexpectedErrorCode()
    {
        var diagnostics = new List<(string Stage, string Code)>();
        var persistence = new TestSettingsPersistence
        {
            WriteException = new InvalidOperationException("private implementation detail")
        };
        var coordinator = new ViewerSettingsSaveCoordinator(
            new ViewerSettingsStore("viewer-settings.json", persistence),
            (stage, code) => diagnostics.Add((stage, code)));

        var saved = coordinator.TrySave(
            new ViewerSettings(),
            "settings-save-background",
            out var errorCode);
        var failure = Assert.Throws<AgentClientException>(() =>
            coordinator.SaveOrThrow(
                new ViewerSettings(),
                "settings-save-connection"));

        Assert.False(saved);
        Assert.Equal("VIEWER_UNEXPECTED_ERROR", errorCode);
        Assert.Equal("VIEWER_UNEXPECTED_ERROR", failure.ErrorCode);
        Assert.All(
            diagnostics,
            item => Assert.Equal("VIEWER_UNEXPECTED_ERROR", item.Code));
    }

    [Fact]
    public void SaveCoordinator_SaveOrThrowPreservesFailClosedConnectionFlow()
    {
        var diagnostics = new List<(string Stage, string Code)>();
        var persistence = new TestSettingsPersistence
        {
            WriteException = new UnauthorizedAccessException("simulated")
        };
        var coordinator = new ViewerSettingsSaveCoordinator(
            new ViewerSettingsStore("viewer-settings.json", persistence),
            (stage, code) => diagnostics.Add((stage, code)));

        var failure = Assert.Throws<AgentClientException>(() =>
            coordinator.SaveOrThrow(
                new ViewerSettings(),
                "settings-save-connection"));

        Assert.Equal("VIEWER_SETTINGS_WRITE_FAILED", failure.ErrorCode);
        Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
        Assert.Equal(
            ("settings-save-connection", "VIEWER_SETTINGS_WRITE_FAILED"),
            Assert.Single(diagnostics));
    }

    [Fact]
    public async Task SaveCoordinator_ConcurrentWritersAreSerializedAndLaterSnapshotWins()
    {
        var persistence = new BlockingSettingsPersistence();
        var store = new ViewerSettingsStore("viewer-settings.json", persistence);
        var coordinator = new ViewerSettingsSaveCoordinator(store);
        var first = new ViewerSettings
        {
            AgentUri = "https://first.example.test:18443",
            MiniTopmost = false
        };
        first.SetEventCursor("agent", 10);
        var second = new ViewerSettings
        {
            AgentUri = "https://second.example.test:18443",
            MiniTopmost = true
        };
        second.SetEventCursor("agent", 20);

        var firstSave = Task.Factory.StartNew(
            () => coordinator.TrySave(
                first,
                "settings-save-background",
                out _),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(persistence.FirstWriteEntered.Wait(TimeSpan.FromSeconds(2)));

        var secondCallStarted = new ManualResetEventSlim();
        var secondSave = Task.Factory.StartNew(
            () =>
            {
                secondCallStarted.Set();
                coordinator.SaveOrThrow(second, "settings-save-shutdown");
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(secondCallStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(persistence.SecondWriteEntered.Wait(TimeSpan.FromMilliseconds(250)));

        persistence.ReleaseFirstWrite.Set();
        Assert.True(await firstSave.WaitAsync(TimeSpan.FromSeconds(2)));
        await secondSave.WaitAsync(TimeSpan.FromSeconds(2));

        var loaded = store.Load();
        Assert.Equal(1, persistence.MaximumConcurrentWrites);
        Assert.Equal("https://second.example.test:18443", loaded.AgentUri);
        Assert.True(loaded.MiniTopmost);
        Assert.True(loaded.TryGetEventCursor("agent", out var cursor));
        Assert.Equal(20, cursor);
    }

    [Fact]
    public async Task SaveCoordinator_FirstWriteFailureReleasesSerializationLock()
    {
        var persistence = new BlockingSettingsPersistence
        {
            FirstWriteException = new IOException("simulated first write failure")
        };
        var store = new ViewerSettingsStore("viewer-settings.json", persistence);
        var coordinator = new ViewerSettingsSaveCoordinator(store);
        var first = new ViewerSettings { AgentUri = "https://first.example.test:18443" };
        var second = new ViewerSettings { AgentUri = "https://second.example.test:18443" };

        var firstSave = Task.Factory.StartNew(
            () => coordinator.TrySave(
                first,
                "settings-save-background",
                out var errorCode)
                ? string.Empty
                : errorCode,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(persistence.FirstWriteEntered.Wait(TimeSpan.FromSeconds(2)));

        var secondSave = Task.Factory.StartNew(
            () => coordinator.SaveOrThrow(second, "settings-save-shutdown"),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        persistence.ReleaseFirstWrite.Set();

        Assert.Equal(
            "VIEWER_SETTINGS_WRITE_FAILED",
            await firstSave.WaitAsync(TimeSpan.FromSeconds(2)));
        await secondSave.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, persistence.MaximumConcurrentWrites);
        Assert.Equal("https://second.example.test:18443", store.Load().AgentUri);
    }

    [Fact]
    public async Task Sanitize_WaitsForSynchronizedTrustPinMutationAndCopiesStableSnapshot()
    {
        var settings = new ViewerSettings
        {
            AgentUri = "https://agent.example.test:18443"
        };
        var mutationEntered = new ManualResetEventSlim();
        var releaseMutation = new ManualResetEventSlim();
        var sanitizeStarted = new ManualResetEventSlim();
        var pin = new string('A', 64);

        var mutation = Task.Factory.StartNew(
            () => settings.Synchronize(current =>
            {
                mutationEntered.Set();
                Assert.True(releaseMutation.Wait(TimeSpan.FromSeconds(2)));
                current.AgentTrustPins[current.BuildAgentAuthority()] = pin;
            }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(2)));

        var sanitize = Task.Factory.StartNew(
            () =>
            {
                sanitizeStarted.Set();
                return ViewerSettingsSanitizer.Sanitize(settings);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            Assert.True(sanitizeStarted.Wait(TimeSpan.FromSeconds(2)));
            var completed = await Task.WhenAny(sanitize, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(sanitize, completed);
        }
        finally
        {
            releaseMutation.Set();
        }

        await mutation.WaitAsync(TimeSpan.FromSeconds(2));
        var clean = await sanitize.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(clean.TryGetAgentTrustPin(out var stored));
        Assert.Equal(pin, stored);
    }

    [Fact]
    public void StartupWindowPolicy_HonorsTrayStartUnlessConnectionNeedsAttention()
    {
        var settings = new ViewerSettings { StartMinimizedToTray = true };

        Assert.False(StartupWindowPolicy.ShouldShowMainWindow(settings, needsConnection: false));
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow(settings, needsConnection: true));

        settings.StartMinimizedToTray = false;
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow(settings, needsConnection: false));
    }

    [Fact]
    public void Sanitize_PreservesStartMinimizedToTray()
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { StartMinimizedToTray = true });
        Assert.True(clean.StartMinimizedToTray);
    }

    private sealed class TestSettingsPersistence : IViewerSettingsPersistence
    {
        public string? Content { get; set; }
        public Exception? ReadException { get; init; }
        public Exception? WriteException { get; set; }
        public Exception? QuarantineException { get; init; }
        public int WriteCount { get; private set; }
        public int QuarantineCount { get; private set; }

        public string? ReadIfExists(string path)
        {
            if (ReadException is not null) throw ReadException;
            return Content;
        }

        public void WriteAtomically(string path, string content)
        {
            WriteCount++;
            if (WriteException is not null) throw WriteException;
            Content = content;
        }

        public void Quarantine(string path, string destination)
        {
            QuarantineCount++;
            if (QuarantineException is not null) throw QuarantineException;
            Content = null;
        }
    }

    private sealed class BlockingSettingsPersistence : IViewerSettingsPersistence
    {
        private readonly object _contentSync = new();
        private int _activeWrites;
        private int _maximumConcurrentWrites;
        private int _writeCount;
        private string? _content;

        public ManualResetEventSlim FirstWriteEntered { get; } = new();
        public ManualResetEventSlim SecondWriteEntered { get; } = new();
        public ManualResetEventSlim ReleaseFirstWrite { get; } = new();
        public Exception? FirstWriteException { get; init; }
        public int MaximumConcurrentWrites => Volatile.Read(ref _maximumConcurrentWrites);

        public string? ReadIfExists(string path)
        {
            lock (_contentSync) return _content;
        }

        public void WriteAtomically(string path, string content)
        {
            var writeNumber = Interlocked.Increment(ref _writeCount);
            var active = Interlocked.Increment(ref _activeWrites);
            UpdateMaximum(active);
            try
            {
                if (writeNumber == 1)
                {
                    FirstWriteEntered.Set();
                    if (!ReleaseFirstWrite.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("test write gate timed out");
                    }
                    if (FirstWriteException is not null) throw FirstWriteException;
                }
                else if (writeNumber == 2)
                {
                    SecondWriteEntered.Set();
                }

                lock (_contentSync) _content = content;
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }

        public void Quarantine(string path, string destination)
        {
            lock (_contentSync) _content = null;
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentWrites);
                if (active <= current
                    || Interlocked.CompareExchange(
                        ref _maximumConcurrentWrites,
                        active,
                        current) == current)
                {
                    return;
                }
            }
        }
    }
}
