using System.IO;
using System.Net.Security;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerManagedDeviceTests
{
    [Fact]
    public void DeviceStore_EncryptsSecretsPreservesBlankEditsAndDefaultsMonitoringOff()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "devices.json");
            var store = new ManagedDeviceStore(path, new TestProtector());
            var draft = Draft("login-secret", "enable-secret");
            draft.MonitoringEnabled = true;
            draft.ConnectionVerified = false;
            var saved = store.Save(draft);

            Assert.False(saved.MonitoringEnabled);
            Assert.False(saved.ConnectionVerified);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("login-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("enable-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", json, StringComparison.Ordinal);
            Assert.Equal(new ManagedDeviceSecrets("operator", "login-secret", "enable-secret"), store.GetSecrets(saved.Id));

            var updated = store.Save(new ManagedDeviceDraft
            {
                Id = saved.Id,
                DisplayName = "ACCESS-SW-01-R",
                Model = saved.Model,
                Host = saved.Host,
                Username = store.CreateEditDraft(saved.Id).Username
            });
            Assert.Equal("ACCESS-SW-01-R", updated.DisplayName);
            Assert.Equal(new ManagedDeviceSecrets("operator", "login-secret", "enable-secret"), store.GetSecrets(saved.Id));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DeviceStore_SaveWithOutcome_ReportsConnectionIdentityChanges()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ManagedDeviceStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector());

            var created = store.SaveWithOutcome(Draft("pw", null));
            Assert.True(created.ConnectionIdentityChanged);

            var displayOnly = store.CreateEditDraft(created.Profile.Id);
            displayOnly.DisplayName = "ACCESS-SW-RENAMED";
            var renamed = store.SaveWithOutcome(displayOnly);
            Assert.False(renamed.ConnectionIdentityChanged);
            Assert.Equal("ACCESS-SW-RENAMED", renamed.Profile.DisplayName);

            var endpointEdit = store.CreateEditDraft(created.Profile.Id);
            endpointEdit.Host = "192.0.2.11";
            var endpointChanged = store.SaveWithOutcome(endpointEdit);
            Assert.True(endpointChanged.ConnectionIdentityChanged);
            Assert.Equal("192.0.2.11", endpointChanged.Profile.Host);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DeviceStore_MigratesLegacyPlainUsernameToProtectedValue()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "devices.json");
            File.WriteAllText(path, """
            {
              "SchemaVersion":1,
              "Devices":[{
                "Id":"legacy",
                "DisplayName":"ACCESS-SW-LEGACY",
                "Model":"IES4224GP",
                "Host":"192.0.2.20",
                "Port":23,
                "Username":"legacy-operator",
                "ProtectedPassword":"cHJvdGVjdGVkOnB3",
                "MonitoringEnabled":false,
                "ConnectionVerified":false
              }]
            }
            """);
            var store = new ManagedDeviceStore(path, new TestProtector());

            var profile = Assert.Single(store.Load());

            Assert.Equal(ManagedDeviceLoadStatus.Ok, store.LastLoadStatus);
            Assert.Equal("legacy-operator", store.CreateEditDraft(profile.Id).Username);
            var migrated = File.ReadAllText(path);
            Assert.DoesNotContain("legacy-operator", migrated, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Username\"", migrated, StringComparison.Ordinal);
            Assert.Contains("\"ProtectedUsername\"", migrated, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(PlatformNotSupportedException))]
    public void DeviceStore_LegacyUsernameProtectionFailurePreservesSourceAndFailsClosed(
        Type exceptionType)
    {
        const string original = """
        {
          "SchemaVersion":1,
          "Devices":[{
            "Id":"legacy",
            "DisplayName":"ACCESS-SW-LEGACY",
            "Model":"IES4224GP",
            "Host":"192.0.2.20",
            "Port":23,
            "Username":"legacy-operator",
            "ProtectedPassword":"cHJvdGVjdGVkOnB3",
            "MonitoringEnabled":false,
            "ConnectionVerified":false
          }]
        }
        """;
        var persistence = new TestManagedDevicePersistence { Content = original };
        var protectionFailure = (Exception)Activator.CreateInstance(
            exceptionType,
            "simulated credential protection failure")!;
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new ThrowingProtectProtector(protectionFailure),
            persistence);

        var first = store.LoadWithStatus();
        var second = store.LoadWithStatus();

        Assert.Empty(first.Devices);
        Assert.Empty(second.Devices);
        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, first.Status);
        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, second.Status);
        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_DEVICE_STORE_UNAVAILABLE", store.LoadErrorCode);
        Assert.Equal(1, persistence.ReadCount);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
        AssertAllStoreOperationsBlocked(
            store,
            "VIEWER_DEVICE_STORE_UNAVAILABLE");
        Assert.Equal(0, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
    }

    public static TheoryData<string> InvalidDeviceStoreDocuments => new()
    {
        "null",
        """{"SchemaVersion":1,"Devices":null}""",
        """
        {
          "SchemaVersion":1,
          "Devices":[
            {
              "Id":"valid",
              "DisplayName":"ACCESS-SW-VALID",
              "Model":"IES4224GP",
              "Host":"192.0.2.20",
              "Port":23,
              "ProtectedUsername":"cHJvdGVjdGVkOm9wZXJhdG9y",
              "ProtectedPassword":"cHJvdGVjdGVkOnB3"
            },
            {
              "Id":"invalid",
              "DisplayName":"ACCESS-SW-INVALID",
              "Model":"IES4224GP",
              "Host":"",
              "Port":23,
              "ProtectedUsername":"cHJvdGVjdGVkOm9wZXJhdG9y",
              "ProtectedPassword":"cHJvdGVjdGVkOnB3"
            }
          ]
        }
        """
    };

    [Theory]
    [MemberData(nameof(InvalidDeviceStoreDocuments))]
    public void DeviceStore_InvalidEnvelopeIsQuarantinedWithoutPartialDeviceLoad(string content)
    {
        var persistence = new TestManagedDevicePersistence { Content = content };
        var store = new ManagedDeviceStore("viewer-devices.json", new TestProtector(), persistence);

        var result = store.LoadWithStatus();

        Assert.Empty(result.Devices);
        Assert.Equal(ManagedDeviceLoadStatus.Corrupt, result.Status);
        Assert.Equal(ManagedDeviceLoadStatus.Corrupt, store.LastLoadStatus);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Null(persistence.Content);
    }

    [Fact]
    public void DeviceStore_LoadWithStatusDistinguishesMissingFromValidEmptyStore()
    {
        var persistence = new TestManagedDevicePersistence();
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);

        var missing = store.LoadWithStatus();
        persistence.Content = """{"SchemaVersion":1,"Devices":[]}""";
        var valid = store.LoadWithStatus();

        Assert.Empty(missing.Devices);
        Assert.Equal(ManagedDeviceLoadStatus.Missing, missing.Status);
        Assert.Empty(valid.Devices);
        Assert.Equal(ManagedDeviceLoadStatus.Ok, valid.Status);
    }

    [Fact]
    public void DeviceStore_MissingAfterObservedFileLatchesStorageUnavailable()
    {
        var persistence = new TestManagedDevicePersistence
        {
            Content = """{"SchemaVersion":1,"Devices":[]}"""
        };
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);

        Assert.Equal(ManagedDeviceLoadStatus.Ok, store.LoadWithStatus().Status);
        persistence.Content = null;

        var missingAfterObserved = store.LoadWithStatus();
        var repeated = store.LoadWithStatus();

        Assert.Empty(missingAfterObserved.Devices);
        Assert.Empty(repeated.Devices);
        Assert.Equal(
            ManagedDeviceLoadStatus.StorageUnavailable,
            missingAfterObserved.Status);
        Assert.Equal(
            ManagedDeviceLoadStatus.StorageUnavailable,
            repeated.Status);
        Assert.False(store.IsOperational);
        Assert.Equal(
            "VIEWER_DEVICE_STORE_UNAVAILABLE",
            store.LoadErrorCode);
        Assert.Equal(2, persistence.ReadCount);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
    }

    [Fact]
    public void DeviceStore_MissingAfterSuccessfulSaveLatchesStorageUnavailable()
    {
        var persistence = new TestManagedDevicePersistence();
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);

        _ = store.Save(Draft("pw", null));
        persistence.Content = null;

        var result = store.LoadWithStatus();

        Assert.Empty(result.Devices);
        Assert.Equal(
            ManagedDeviceLoadStatus.StorageUnavailable,
            result.Status);
        Assert.False(store.IsOperational);
        Assert.Equal(
            "VIEWER_DEVICE_STORE_UNAVAILABLE",
            store.LoadErrorCode);
        Assert.Equal(2, persistence.ReadCount);
        Assert.Equal(1, persistence.WriteCount);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void DeviceStore_ReadFailurePreservesOriginalAndDoesNotQuarantine(Type exceptionType)
    {
        const string original = """{"SchemaVersion":1,"Devices":[]}""";
        var persistence = new TestManagedDevicePersistence
        {
            Content = original,
            ReadException = (Exception)Activator.CreateInstance(exceptionType, "simulated storage failure")!
        };
        var store = new ManagedDeviceStore("viewer-devices.json", new TestProtector(), persistence);

        var result = store.LoadWithStatus();

        Assert.Empty(result.Devices);
        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, result.Status);
        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, store.LastLoadStatus);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(original, persistence.Content);
    }

    [Fact]
    public void DeviceStore_FutureSchemaIsPreservedAndLatchedWithoutQuarantine()
    {
        const string original =
            """{"SchemaVersion":2,"Devices":{"FutureShape":true}}""";
        var persistence = new TestManagedDevicePersistence { Content = original };
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);

        var first = store.LoadWithStatus();
        var second = store.LoadWithStatus();

        Assert.Empty(first.Devices);
        Assert.Empty(second.Devices);
        Assert.Equal(ManagedDeviceLoadStatus.VersionUnsupported, first.Status);
        Assert.Equal(ManagedDeviceLoadStatus.VersionUnsupported, second.Status);
        Assert.Equal(ManagedDeviceLoadStatus.VersionUnsupported, store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED", store.LoadErrorCode);
        Assert.Equal(1, persistence.ReadCount);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
    }

    [Fact]
    public void DeviceStore_CorruptStateRemainsLatchedUntilAStoreRestart()
    {
        var persistence = new TestManagedDevicePersistence { Content = "null" };
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);

        var first = store.LoadWithStatus();
        var second = store.LoadWithStatus();

        Assert.Equal(ManagedDeviceLoadStatus.Corrupt, first.Status);
        Assert.Equal(ManagedDeviceLoadStatus.Corrupt, second.Status);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_DEVICE_STORE_CORRUPT", store.LoadErrorCode);
        Assert.Equal(1, persistence.ReadCount);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
        Assert.Null(persistence.Content);

        var restarted = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);
        Assert.Equal(ManagedDeviceLoadStatus.Missing, restarted.LoadWithStatus().Status);
        Assert.True(restarted.IsOperational);
        Assert.Null(restarted.LoadErrorCode);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void DeviceStore_QuarantineFailurePreservesOriginalAndLatchesStorageUnavailable(
        Type exceptionType)
    {
        const string original = "null";
        var persistence = new TestManagedDevicePersistence
        {
            Content = original,
            QuarantineException = (Exception)Activator.CreateInstance(
                exceptionType,
                "simulated quarantine failure")!
        };
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);

        var first = store.LoadWithStatus();
        var second = store.LoadWithStatus();

        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, first.Status);
        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, second.Status);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_DEVICE_STORE_UNAVAILABLE", store.LoadErrorCode);
        Assert.Equal(1, persistence.ReadCount);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
        AssertAllStoreOperationsBlocked(
            store,
            "VIEWER_DEVICE_STORE_UNAVAILABLE");
        Assert.Equal(0, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
    }

    [Theory]
    [InlineData(
        """{"SchemaVersion":2,"Devices":[]}""",
        "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED")]
    [InlineData("null", "VIEWER_DEVICE_STORE_CORRUPT")]
    public void DeviceStore_LatchedFormatFailureBlocksMutationsAndSecretOperations(
        string content,
        string expectedErrorCode)
    {
        var persistence = new TestManagedDevicePersistence { Content = content };
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);
        _ = store.LoadWithStatus();
        var original = persistence.Content;
        var readsAfterFailure = persistence.ReadCount;

        AssertAllStoreOperationsBlocked(store, expectedErrorCode);

        Assert.Equal(readsAfterFailure, persistence.ReadCount);
        Assert.Equal(0, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void DeviceStore_WriteFailurePreservesOriginalAndLatchesStorageUnavailable(
        Type exceptionType)
    {
        const string original = """{"SchemaVersion":1,"Devices":[]}""";
        var persistence = new TestManagedDevicePersistence
        {
            Content = original,
            WriteException = (Exception)Activator.CreateInstance(
                exceptionType,
                "simulated atomic write failure")!
        };
        var store = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);

        _ = Assert.Throws(
            exceptionType,
            () => store.Save(Draft("pw", null)));

        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_DEVICE_STORE_UNAVAILABLE", store.LoadErrorCode);
        Assert.Equal(1, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);

        persistence.WriteException = null;
        AssertAllStoreOperationsBlocked(
            store,
            "VIEWER_DEVICE_STORE_UNAVAILABLE");
        Assert.Equal(1, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
    }

    [Fact]
    public async Task Dashboard_ShowsDeviceStoreCorruptionInsteadOfAnOrdinaryEmptyList()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence { Content = "null" };
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()),
                deviceStore: devices);
            try
            {
                await viewModel.InitializeAsync();

                Assert.Empty(viewModel.Devices);
                Assert.Contains("VIEWER_DEVICE_STORE_CORRUPT", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Contains("다시 등록", viewModel.OperationMessage, StringComparison.Ordinal);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Dashboard_ShowsDeviceStoreIoFailureWithoutCallingItCorrupt()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence
            {
                Content = """{"SchemaVersion":1,"Devices":[]}""",
                ReadException = new IOException("simulated storage failure")
            };
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()),
                deviceStore: devices);
            try
            {
                await viewModel.InitializeAsync();

                Assert.Empty(viewModel.Devices);
                Assert.Contains("VIEWER_DEVICE_STORE_UNAVAILABLE", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("VIEWER_DEVICE_STORE_CORRUPT", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Equal(0, persistence.QuarantineCount);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DeviceStore_SaveFailureDoesNotReplacePersistedDevicesAndRequiresRestart()
    {
        var persistence = new TestManagedDevicePersistence();
        var store = new ManagedDeviceStore("viewer-devices.json", new TestProtector(), persistence);
        var saved = store.Save(Draft("pw", null));
        var previous = persistence.Content;
        var edit = store.CreateEditDraft(saved.Id);
        edit.DisplayName = "ACCESS-SW-CHANGED";
        persistence.WriteException = new IOException("simulated atomic write failure");

        Assert.Throws<IOException>(() => store.Save(edit));

        Assert.Equal(previous, persistence.Content);
        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, store.LastLoadStatus);
        Assert.Empty(store.Load());
        persistence.WriteException = null;
        var restarted = new ManagedDeviceStore(
            "viewer-devices.json",
            new TestProtector(),
            persistence);
        Assert.Equal("ACCESS-SW-01", Assert.Single(restarted.Load()).DisplayName);
    }

    [Fact]
    public async Task Dashboard_PreservesExistingDevicesWhenARefreshCannotReadTheStore()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var saved = devices.Save(Draft("pw", null));
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()),
                deviceStore: devices);
            try
            {
                viewModel.ReloadManagedDevices(saved.Id);
                var original = Assert.Single(viewModel.Devices);
                persistence.ReadException =
                    new IOException("private path host=192.0.2.10 password=secret");

                viewModel.ReloadManagedDevices(saved.Id);

                Assert.Same(original, Assert.Single(viewModel.Devices));
                Assert.Same(original, viewModel.SelectedDevice);
                Assert.Contains(
                    "VIEWER_DEVICE_STORE_UNAVAILABLE",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "192.0.2.10",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "secret",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
            }
            finally
            {
                persistence.ReadException = null;
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Dashboard_ClientSwitchPreservesDevicesAndStoreWarningOnReadFailure()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var saved = devices.Save(Draft("pw", null));
            var client = new StatelessFakeClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(client),
                deviceStore: devices);
            try
            {
                await viewModel.InitializeAsync();
                var original = Assert.Single(viewModel.Devices);
                viewModel.SelectedDevice = original;
                persistence.ReadException =
                    new IOException("private path host=192.0.2.10 password=secret");

                await viewModel.SwitchClientAsync(
                    new ViewerSettings { DemoMode = true });

                Assert.Same(original, Assert.Single(viewModel.Devices));
                Assert.Same(original, viewModel.SelectedDevice);
                Assert.Equal(saved.Id, original.Id);
                Assert.Contains(
                    "VIEWER_DEVICE_STORE_UNAVAILABLE",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "Agent 연결 설정을 저장했습니다.",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
            }
            finally
            {
                persistence.ReadException = null;
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Dashboard_LegacyToStatelessSwitchRemovesStaleDevicesWhenStoreFails()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var now = DateTimeOffset.UtcNow;
            var legacy = new LegacyFakeClient(new AgentSnapshotDto(
                now,
                AgentConnectionState.Connected,
                [
                    new DeviceSnapshotDto(
                        "legacy-device",
                        "LEGACY-SW",
                        "IES4224GP",
                        "비공개",
                        DeviceHealth.Normal,
                        now,
                        "정상",
                        "1일")
                ],
                0,
                "legacy",
                "legacy collector"));
            var replacement = new StatelessFakeClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new QueueClientFactory(legacy, replacement),
                deviceStore: devices);
            try
            {
                await viewModel.InitializeAsync();
                Assert.Single(viewModel.Devices);
                Assert.Equal(1, viewModel.NormalCount);

                persistence.ReadException =
                    new IOException("private path host=192.0.2.10 password=secret");

                await viewModel.SwitchClientAsync(
                    new ViewerSettings { DemoMode = true });

                Assert.Empty(viewModel.Devices);
                Assert.Null(viewModel.SelectedDevice);
                Assert.Equal(0, viewModel.NormalCount);
                Assert.Equal(0, viewModel.MonitoredCount);
                Assert.False(viewModel.ReadOnlyQueriesEnabled);
                Assert.Equal(DeviceHealth.Warning, viewModel.MiniIssueHealth);
                Assert.Contains(
                    "VIEWER_DEVICE_STORE_UNAVAILABLE",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
            }
            finally
            {
                persistence.ReadException = null;
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Dashboard_ManualRefreshPreservesDevicesAndStoreWarningOnReadFailure()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            _ = devices.Save(Draft("pw", null));
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()),
                deviceStore: devices);
            try
            {
                await viewModel.InitializeAsync();
                var original = Assert.Single(viewModel.Devices);
                viewModel.SelectedDevice = original;
                persistence.ReadException =
                    new IOException("private path host=192.0.2.10 password=secret");

                viewModel.RefreshCommand.Execute(null);
                await WaitUntilAsync(() =>
                    !viewModel.IsBusy
                    && viewModel.OperationMessage.Contains(
                        "VIEWER_DEVICE_STORE_UNAVAILABLE",
                        StringComparison.Ordinal));

                Assert.Same(original, Assert.Single(viewModel.Devices));
                Assert.Same(original, viewModel.SelectedDevice);
                Assert.DoesNotContain(
                    "목록을 새로고침했습니다.",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
            }
            finally
            {
                persistence.ReadException = null;
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Dashboard_GetManagedDevicesWithoutAStorePreservesTheEmptyListContract()
    {
        var folder = TemporaryFolder();
        try
        {
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()));
            try
            {
                Assert.Empty(viewModel.GetManagedDevices());
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Dashboard_GetManagedDevicesReportsFutureSchemaAsVersionUnsupported()
    {
        var folder = TemporaryFolder();
        try
        {
            const string original =
                """{"SchemaVersion":2,"Devices":{"FutureShape":true}}""";
            var persistence = new TestManagedDevicePersistence { Content = original };
            var store = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()),
                deviceStore: store);
            try
            {
                var exception = Assert.Throws<InvalidDataException>(
                    () => viewModel.GetManagedDevices());

                Assert.Equal(
                    "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED",
                    exception.Message);
                Assert.Equal(original, persistence.Content);
                Assert.Equal(0, persistence.QuarantineCount);
                Assert.Equal(0, persistence.WriteCount);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task SaveManagedDevice_MonitoringCleanupFailureKeepsCommittedDeviceAndReportsWarning()
    {
        var folder = TemporaryFolder();
        try
        {
            var deviceStore = new ManagedDeviceStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector());
            var draft = Draft("pw", null);
            draft.ConnectionVerified = true;
            draft.MonitoringEnabled = false;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            draft.LastConnectionTestCode = "OK";
            var saved = deviceStore.Save(draft);
            var monitoringPersistence = new TestMonitoringPersistence();
            var monitoringStore = new ViewerMonitoringStore(
                Path.Combine(folder, "monitor.json"),
                monitoringPersistence);
            monitoringStore.RecordCapability(
                saved.Id,
                new CollectorCapabilityDto(
                    "interface_status",
                    true,
                    "Supported"));
            var diagnostics = new List<(string Stage, string ErrorCode)>();
            var settingsStore =
                new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                settingsStore,
                clientFactory: null,
                synchronizationContext: null,
                deviceStore,
                monitoringStore,
                new ViewerSettingsSaveCoordinator(settingsStore),
                (stage, errorCode) => diagnostics.Add((stage, errorCode)),
                static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
            try
            {
                var edit = deviceStore.CreateEditDraft(saved.Id);
                edit.DisplayName = "ACCESS-SW-SAVED";
                monitoringPersistence.WriteException =
                    new IOException("private path host=192.0.2.10 password=secret");

                var result = viewModel.SaveManagedDevice(edit, out var warningCode);

                Assert.Equal("ACCESS-SW-SAVED", result.DisplayName);
                Assert.Equal(
                    "ACCESS-SW-SAVED",
                    Assert.Single(deviceStore.Load()).DisplayName);
                Assert.Equal(
                    "VIEWER_MONITOR_STATE_WRITE_FAILED",
                    warningCode);
                Assert.Contains(
                    "VIEWER_MONITOR_STATE_WRITE_FAILED",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.Contains(
                    ("device-management-save", "VIEWER_MONITOR_STATE_WRITE_FAILED"),
                    diagnostics);
                Assert.Single(monitoringStore.LoadCapabilities(saved.Id));
                Assert.DoesNotContain(
                    diagnostics,
                    entry => entry.Stage.Contains("192.0.2.10", StringComparison.Ordinal)
                             || entry.ErrorCode.Contains("secret", StringComparison.Ordinal));
            }
            finally
            {
                monitoringPersistence.WriteException = null;
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FailedConnectionTest_ForcesMonitoringOffEvenWhenConnectionFieldsAreUnchanged()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ManagedDeviceStore(Path.Combine(folder, "devices.json"), new TestProtector());
            var draft = Draft("pw", null);
            draft.ConnectionVerified = true;
            draft.MonitoringEnabled = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            draft.LastConnectionTestCode = "OK";
            var verified = store.Save(draft);
            Assert.True(verified.MonitoringEnabled);

            var failed = store.Save(new ManagedDeviceDraft
            {
                Id = verified.Id,
                DisplayName = verified.DisplayName,
                Model = verified.Model,
                Host = verified.Host,
                Username = store.CreateEditDraft(verified.Id).Username,
                ConnectionVerified = false,
                MonitoringEnabled = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow,
                LastConnectionTestCode = "AUTH_FAILED"
            });

            Assert.False(failed.ConnectionVerified);
            Assert.False(failed.MonitoringEnabled);
            Assert.Equal("AUTH_FAILED", failed.LastConnectionTestCode);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("show port status", true)]
    [InlineData("show running-config", true)]
    [InlineData("show", false)]
    [InlineData("show port status\nreload", false)]
    [InlineData("show port | include up", false)]
    [InlineData("show $secret", false)]
    [InlineData("show port > file", false)]
    [InlineData("configure terminal", false)]
    public void ViewerCommandPolicy_MatchesSharedCorePolicy(string command, bool expected) =>
        Assert.Equal(expected, ManagedDeviceValidator.IsSingleShowCommand(command));

    [Fact]
    public void MonitoringStore_DeduplicatesFailuresEmitsRecoveryAndIgnoresSyslogReordering()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));
            Assert.Empty(store.RecordFailure(device, "TCP_TIMEOUT"));

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            Assert.Equal("TCP_TIMEOUT", store.GetActiveFailureCode(device.Id));
            Assert.False(Assert.Single(store.LoadEvents()).Recovered);
            var recovered = store.RecordSuccess(device);
            Assert.Equal(2, recovered.Count);
            Assert.All(recovered, item => Assert.True(item.Recovered));
            Assert.Equal(DeviceHealth.Normal, recovered[^1].Severity);

            Assert.Empty(store.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Empty(store.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((2, "line-b"), (1, "line-a"))));
            var newLog = store.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((3, "line-c"), (2, "line-b"), (1, "line-a")));
            Assert.Single(newLog);
            Assert.Equal("새 로그", newLog[0].Kind);

            var json = File.ReadAllText(Path.Combine(folder, "monitor.json"));
            Assert.DoesNotContain("line-a", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Port 1 Up", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_FailureCodeChangeUpdatesOneActiveIncidentUntilSuccess()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            var initial = Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));
            var changed = Assert.Single(store.RecordFailure(device, "TELNET_SESSION_CLOSED"));

            Assert.Equal(initial.AgentEventId, changed.AgentEventId);
            Assert.Equal(initial.Sequence, changed.Sequence);
            Assert.Equal("TELNET_SESSION_CLOSED", changed.Detail);
            Assert.False(changed.Recovered);
            Assert.Equal("TELNET_SESSION_CLOSED", store.GetActiveFailureCode(device.Id));
            var persistedActive = Assert.Single(store.LoadEvents());
            Assert.Equal(initial.AgentEventId, persistedActive.AgentEventId);
            Assert.False(persistedActive.Recovered);

            var recovery = store.RecordSuccess(device);

            Assert.Equal(2, recovery.Count);
            Assert.All(recovery, item => Assert.True(item.Recovered));
            Assert.Null(store.GetActiveFailureCode(device.Id));
            Assert.Equal(2, store.LoadEvents().Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MonitoringStore_GapRebaselinesLegacyStateWithoutReportingStaleChange(int schemaVersion)
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            File.WriteAllText(path, $$"""
            {
              "SchemaVersion": {{schemaVersion}},
              "NextSequence": 0,
              "LastStoppedUtc": "2000-01-01T00:00:00+00:00",
              "Baselines": {
                "sw-01\nSHOW PORT STATUS": {
                  "OutputHash": "legacy-hash",
                  "LineHashes": []
                }
              },
              "Events": []
            }
            """);
            var store = new ViewerMonitoringStore(path);
            var device = Profile();

            Assert.Equal(ViewerMonitoringLoadStatus.Ok, store.LastLoadStatus);
            Assert.True(store.IsOperational);
            Assert.Null(store.LoadErrorCode);

            var gap = Assert.Single(store.BeginSession([device]));
            Assert.Equal("감시 공백", gap.Kind);
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            var currentChange = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));

            Assert.Equal("포트 상태", currentChange.Kind);
            Assert.Contains("\"SchemaVersion\": 3", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_InterfaceLifecycleIsSemanticDeduplicatedAndRecoverable()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up"))));
            var opened = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));

            Assert.Equal(DeviceHealth.Warning, opened.Severity);
            Assert.Equal("포트 상태", opened.Kind);
            Assert.Equal("Port 24 Link Down", opened.Title);
            Assert.Contains("영향 대상은 지정되지 않았습니다", opened.Detail, StringComparison.Ordinal);
            Assert.True(opened.IsActiveCondition);
            Assert.Equal(1, store.GetActiveInterfaceConditionCount(device.Id));

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            Assert.Single(store.LoadEvents());

            var recovered = store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up")));

            Assert.Equal(2, recovered.Count);
            Assert.All(recovered, item => Assert.True(item.Recovered));
            Assert.Equal("복구", recovered[^1].Kind);
            Assert.Equal(0, store.GetActiveInterfaceConditionCount(device.Id));
            Assert.Equal(2, store.LoadEvents().Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_RetentionPreservesActiveConditionsUntilRecovery()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            var device = Profile();
            var store = new ViewerMonitoringStore(path);

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up"))));
            var interfaceEvent = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            var failureEvent = Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));
            store.EndSession();

            var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            state["LastStoppedUtc"] = "2000-01-01T00:00:00+00:00";
            state["LastHeartbeatUtc"] = "2000-01-01T00:00:00+00:00";
            File.WriteAllText(
                path,
                state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var restarted = new ViewerMonitoringStore(path);
            var fillerDevices = Enumerable.Range(1, 600)
                .Select(index => new ManagedDeviceProfile
                {
                    Id = $"filler-{index:D4}",
                    DisplayName = $"FILLER-{index:D4}",
                    Model = "IES4224GP",
                    Host = "192.0.2.20",
                    Port = 23,
                    ProtectedUsername = "protected",
                    ProtectedPassword = "protected",
                    ConnectionVerified = true,
                    MonitoringEnabled = true
                })
                .ToArray();

            var gapEvents = restarted.BeginSession(fillerDevices);
            var retained = restarted.LoadEvents();

            Assert.Equal(600, gapEvents.Count);
            Assert.Equal(500, retained.Count);
            Assert.Contains(retained, item => item.AgentEventId == interfaceEvent.AgentEventId);
            Assert.Contains(retained, item => item.AgentEventId == failureEvent.AgentEventId);
            Assert.DoesNotContain(retained, item => item.AgentEventId == gapEvents[0].AgentEventId);
            Assert.Contains(retained, item => item.AgentEventId == gapEvents[^1].AgentEventId);
            Assert.True(restarted.Acknowledge(interfaceEvent.AgentEventId));
            Assert.True(restarted.Acknowledge(failureEvent.AgentEventId));

            var reloaded = new ViewerMonitoringStore(path);
            Assert.Equal(1, reloaded.GetActiveInterfaceConditionCount(device.Id));
            Assert.Equal("TCP_TIMEOUT", reloaded.GetActiveFailureCode(device.Id));
            Assert.True(reloaded.LoadEvents().Single(item =>
                item.AgentEventId == interfaceEvent.AgentEventId).Acknowledged);
            Assert.True(reloaded.LoadEvents().Single(item =>
                item.AgentEventId == failureEvent.AgentEventId).Acknowledged);

            var changedFailure = Assert.Single(
                reloaded.RecordFailure(device, "TELNET_SESSION_CLOSED"));
            Assert.Equal(failureEvent.AgentEventId, changedFailure.AgentEventId);
            Assert.Equal("TELNET_SESSION_CLOSED", changedFailure.Detail);

            var interfaceRecovery = reloaded.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up")));
            var failureRecovery = reloaded.RecordSuccess(device);

            Assert.Equal(2, interfaceRecovery.Count);
            Assert.Equal(2, failureRecovery.Count);
            Assert.All(interfaceRecovery, item => Assert.True(item.Recovered));
            Assert.All(failureRecovery, item => Assert.True(item.Recovered));
            Assert.Equal(0, reloaded.GetActiveInterfaceConditionCount(device.Id));
            Assert.Null(reloaded.GetActiveFailureCode(device.Id));

            var finalState = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(500, finalState["Events"]!.AsArray().Count);
            Assert.Empty(finalState["ActiveFailures"]!.AsObject());
            Assert.Empty(finalState["ActiveInterfaceConditions"]!.AsObject());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_RetentionNeverDeletesActiveInterfaceConditionsWhenLimitIsExhausted()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            var store = new ViewerMonitoringStore(path);
            var device = Profile();
            var upPorts = Enumerable.Range(1, 501)
                .Select(index => (PortId: index.ToString(), Link: "Up"))
                .ToArray();
            var downPorts = Enumerable.Range(1, 500)
                .Select(index => (PortId: index.ToString(), Link: "Down"))
                .ToArray();

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(upPorts)));
            var interfaceEvents = store.RecordOutput(
                device,
                "show port status",
                PortStatus(downPorts));
            var lastInterfaceEvent = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("501", "Down"))));

            Assert.Equal(500, interfaceEvents.Count);
            var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(501, state["Events"]!.AsArray().Count);
            Assert.Equal(501, state["ActiveInterfaceConditions"]!.AsObject().Count);
            Assert.Empty(state["ActiveFailures"]!.AsObject());
            Assert.True(store.Acknowledge(interfaceEvents[0].AgentEventId));
            Assert.True(store.Acknowledge(lastInterfaceEvent.AgentEventId));

            var reloaded = new ViewerMonitoringStore(path);
            Assert.Equal(501, reloaded.GetActiveInterfaceConditionCount(device.Id));
            var recovery = reloaded.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up")));
            Assert.Equal(2, recovery.Count);
            Assert.All(recovery, item => Assert.True(item.Recovered));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_InitialDownAndPortSetChangesDoNotCreateFalseEvents()
    {
        var folder = TemporaryFolder();
        try
        {
            var device = Profile();
            var initialDownStore = new ViewerMonitoringStore(
                Path.Combine(folder, "initial-down.json"));

            Assert.Empty(initialDownStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            Assert.Empty(initialDownStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            Assert.Empty(initialDownStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            Assert.Equal(0, initialDownStore.GetActiveInterfaceConditionCount(device.Id));

            var changingSetStore = new ViewerMonitoringStore(
                Path.Combine(folder, "changing-set.json"));
            Assert.Empty(changingSetStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"), ("2", "Up"))));
            Assert.Empty(changingSetStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"), ("3", "Down"))));
            Assert.Empty(changingSetStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"), ("2", "Up"), ("3", "Down"))));

            Assert.Empty(changingSetStore.LoadEvents());
            Assert.Equal(0, changingSetStore.GetActiveInterfaceConditionCount(device.Id));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_ParserFailureDoesNotAdvanceInterfaceOrLogBaselines()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            Assert.Empty(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((1, "line-a"))));

            var rejectedInterface = store.TryRecordParsedOutput(
                device,
                "show port status",
                "unrecognized interface response");
            var rejectedLog = store.TryRecordParsedOutput(
                device,
                "show syslog tail num 100",
                "unrecognized log response");

            Assert.False(rejectedInterface.Accepted);
            Assert.False(rejectedLog.Accepted);
            Assert.NotNull(rejectedInterface.ErrorCode);
            Assert.NotNull(rejectedLog.ErrorCode);
            Assert.Empty(store.LoadEvents());

            Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            var log = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Equal("새 시스템 로그 1건", log.Title);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_GapPreservesActiveInterfaceConditionAndRecoversFromFreshBaseline()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            var device = Profile();
            var store = new ViewerMonitoringStore(path);
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up"))));
            Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            store.EndSession();

            var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            state["LastStoppedUtc"] = "2000-01-01T00:00:00+00:00";
            state["LastHeartbeatUtc"] = "2000-01-01T00:00:00+00:00";
            File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var restarted = new ViewerMonitoringStore(path);
            Assert.Single(restarted.BeginSession([device]));
            Assert.Equal(1, restarted.GetActiveInterfaceConditionCount(device.Id));
            Assert.Empty(restarted.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            Assert.Equal(1, restarted.GetActiveInterfaceConditionCount(device.Id));

            var recovered = restarted.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up")));

            Assert.Equal(2, recovered.Count);
            Assert.Equal(0, restarted.GetActiveInterfaceConditionCount(device.Id));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_NullCollectionIsQuarantinedAsCorruptState()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "Baselines": null,
              "ActiveFailures": {},
              "Capabilities": {},
              "Events": []
            }
            """);

            var store = new ViewerMonitoringStore(path);

            Assert.Empty(store.LoadEvents());
            Assert.Equal(ViewerMonitoringLoadStatus.Corrupt, store.LastLoadStatus);
            Assert.False(store.IsOperational);
            Assert.Equal("VIEWER_MONITOR_STATE_CORRUPT", store.LoadErrorCode);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "monitor.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void MonitoringStore_ReadIoFailureIsNonOperationalWithoutQuarantining(
        Type exceptionType)
    {
        var persistence = new TestMonitoringPersistence
        {
            ReadException = (Exception)Activator.CreateInstance(
                exceptionType,
                "simulated storage failure")!
        };

        var store = new ViewerMonitoringStore("monitor.json", persistence);

        Assert.Empty(store.LoadEvents());
        Assert.Equal(
            ViewerMonitoringLoadStatus.StorageUnavailable,
            store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_MONITOR_STATE_UNAVAILABLE", store.LoadErrorCode);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void MonitoringStore_QuarantineFailureIsReportedAsStorageUnavailable(
        Type exceptionType)
    {
        var persistence = new TestMonitoringPersistence
        {
            Content = "{not-json",
            QuarantineException = (Exception)Activator.CreateInstance(
                exceptionType,
                "simulated quarantine failure")!
        };

        var store = new ViewerMonitoringStore("monitor.json", persistence);

        Assert.Empty(store.LoadEvents());
        Assert.Equal(
            ViewerMonitoringLoadStatus.StorageUnavailable,
            store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_MONITOR_STATE_UNAVAILABLE", store.LoadErrorCode);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Equal("{not-json", persistence.Content);
        Assert.Equal(0, persistence.WriteCount);
        var blocked = Assert.Throws<InvalidOperationException>(store.Heartbeat);
        Assert.Equal("VIEWER_MONITOR_STATE_UNAVAILABLE", blocked.Message);
        Assert.Equal(0, persistence.WriteCount);
    }

    [Fact]
    public void MonitoringStore_FutureSchemaIsPreservedAndReportedAsUnsupported()
    {
        const string original = """
        {
          "SchemaVersion": 4,
          "NextSequence": 0,
          "Baselines": {},
          "ActiveFailures": {},
          "ActiveInterfaceConditions": {},
          "Capabilities": {},
          "Events": []
        }
        """;
        var persistence = new TestMonitoringPersistence { Content = original };

        var store = new ViewerMonitoringStore("monitor.json", persistence);

        Assert.Empty(store.LoadEvents());
        Assert.Equal(
            ViewerMonitoringLoadStatus.VersionUnsupported,
            store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal(
            "VIEWER_MONITOR_STATE_VERSION_UNSUPPORTED",
            store.LoadErrorCode);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
        Assert.Equal(original, persistence.Content);
    }

    [Fact]
    public void MonitoringStore_InvalidUtf8PhysicalFileIsQuarantinedAsCorrupt()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            var validSchemaThreeJson = """
            {
              "SchemaVersion": 3,
              "NextSequence": 1,
              "Baselines": {},
              "ActiveFailures": {},
              "ActiveInterfaceConditions": {},
              "Capabilities": {},
              "Events": [{
                "Sequence": 1,
                "AgentEventId": "viewer-1",
                "DeviceId": "sw-01",
                "DeviceName": "ACCESS-SW-01",
                "OccurredAt": "2026-07-28T00:00:00+00:00",
                "Severity": 1,
                "Kind": "test",
                "Title": "test",
                "Detail": "test"
              }]
            }
            """;
            var invalidUtf8 = System.Text.Encoding.UTF8.GetBytes(
                validSchemaThreeJson);
            var deviceNameBytes = System.Text.Encoding.UTF8.GetBytes(
                "ACCESS-SW-01");
            var deviceNameOffset = invalidUtf8.AsSpan().IndexOf(deviceNameBytes);
            Assert.True(deviceNameOffset >= 0);
            invalidUtf8[deviceNameOffset] = 0xC3;
            invalidUtf8[deviceNameOffset + 1] = 0x28;
            File.WriteAllBytes(path, invalidUtf8);

            var store = new ViewerMonitoringStore(path);

            Assert.Empty(store.LoadEvents());
            Assert.Equal(ViewerMonitoringLoadStatus.Corrupt, store.LastLoadStatus);
            Assert.False(store.IsOperational);
            Assert.Equal("VIEWER_MONITOR_STATE_CORRUPT", store.LoadErrorCode);
            Assert.False(File.Exists(path));
            var quarantine = Assert.Single(
                Directory.GetFiles(folder, "monitor.json.corrupt-*"));
            Assert.Equal(invalidUtf8, File.ReadAllBytes(quarantine));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    public static TheoryData<string> RequiredMonitoringStateProperties => new()
    {
        "SchemaVersion",
        "NextSequence",
        "Baselines",
        "ActiveFailures",
        "ActiveInterfaceConditions",
        "Capabilities",
        "Events"
    };

    [Theory]
    [MemberData(nameof(RequiredMonitoringStateProperties))]
    public void MonitoringStore_CurrentSchemaMissingRequiredPropertyIsCorrupt(
        string propertyName)
    {
        var state = JsonNode.Parse(CompleteMonitoringStateJson())!.AsObject();
        Assert.True(state.Remove(propertyName));
        var persistence = new TestMonitoringPersistence
        {
            Content = state.ToJsonString()
        };

        var store = new ViewerMonitoringStore("monitor.json", persistence);

        Assert.Empty(store.LoadEvents());
        Assert.Equal(ViewerMonitoringLoadStatus.Corrupt, store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_MONITOR_STATE_CORRUPT", store.LoadErrorCode);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Null(persistence.Content);
        Assert.Equal(0, persistence.WriteCount);
    }

    [Fact]
    public void MonitoringStore_NextSequenceBelowStoredEventMaximumIsCorrupt()
    {
        var persistence = MonitoringPersistenceWithClosedFailure();
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        state["NextSequence"] = 1;
        persistence.Content = state.ToJsonString();

        var store = new ViewerMonitoringStore("monitor.json", persistence);

        Assert.Empty(store.LoadEvents());
        Assert.Equal(ViewerMonitoringLoadStatus.Corrupt, store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_MONITOR_STATE_CORRUPT", store.LoadErrorCode);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Null(persistence.Content);
    }

    [Fact]
    public void MonitoringStore_DuplicateAgentEventIdIsCorrupt()
    {
        var persistence = MonitoringPersistenceWithClosedFailure();
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        var events = state["Events"]!.AsArray();
        var duplicateId = events[0]!["AgentEventId"]!.GetValue<string>();
        events[1]!.AsObject()["AgentEventId"] = duplicateId;
        persistence.Content = state.ToJsonString();

        var store = new ViewerMonitoringStore("monitor.json", persistence);

        Assert.Empty(store.LoadEvents());
        Assert.Equal(ViewerMonitoringLoadStatus.Corrupt, store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_MONITOR_STATE_CORRUPT", store.LoadErrorCode);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Null(persistence.Content);
    }

    public static TheoryData<string> RequiredMonitoringEventStringProperties => new()
    {
        "DeviceName",
        "Kind",
        "Title",
        "Detail"
    };

    [Theory]
    [MemberData(nameof(RequiredMonitoringEventStringProperties))]
    public void MonitoringStore_CurrentSchemaMissingRequiredEventStringIsCorrupt(
        string propertyName)
    {
        var persistence = MonitoringPersistenceWithClosedFailure();
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        var firstEvent = state["Events"]![0]!.AsObject();
        Assert.True(firstEvent.Remove(propertyName));
        persistence.Content = state.ToJsonString();

        AssertMonitoringStateIsQuarantinedAsCorrupt(persistence);
    }

    [Fact]
    public void MonitoringStore_CurrentSchemaInvalidEventSeverityIsCorrupt()
    {
        var persistence = MonitoringPersistenceWithClosedFailure();
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        state["Events"]![0]!["Severity"] = 999;
        persistence.Content = state.ToJsonString();

        AssertMonitoringStateIsQuarantinedAsCorrupt(persistence);
    }

    [Fact]
    public void MonitoringStore_CurrentSchemaMissingEventOccurrenceIsCorrupt()
    {
        var persistence = MonitoringPersistenceWithClosedFailure();
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        Assert.True(state["Events"]![0]!.AsObject().Remove("OccurredAt"));
        persistence.Content = state.ToJsonString();

        AssertMonitoringStateIsQuarantinedAsCorrupt(persistence);
    }

    [Fact]
    public void MonitoringStore_CurrentSchemaMissingCapabilityStateIsCorrupt()
    {
        var persistence = new TestMonitoringPersistence();
        var source = new ViewerMonitoringStore("monitor.json", persistence);
        source.RecordCapability(
            "sw-01",
            new CollectorCapabilityDto("interface_status", true, "Ready"));
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        var capability = state["Capabilities"]!["sw-01"]![0]!.AsObject();
        Assert.True(capability.Remove("State"));
        persistence.Content = state.ToJsonString();

        AssertMonitoringStateIsQuarantinedAsCorrupt(persistence);
    }

    public static TheoryData<string> ActiveReferenceDamage => new()
    {
        "Recovered",
        "DeviceId",
        "ConditionKey"
    };

    [Theory]
    [MemberData(nameof(ActiveReferenceDamage))]
    public void MonitoringStore_CurrentSchemaInvalidActiveFailureReferenceIsCorrupt(
        string damage)
    {
        var persistence = MonitoringPersistenceWithActiveFailure();
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        DamageActiveEvent(state, damage);
        persistence.Content = state.ToJsonString();

        AssertMonitoringStateIsQuarantinedAsCorrupt(persistence);
    }

    [Theory]
    [MemberData(nameof(ActiveReferenceDamage))]
    public void MonitoringStore_CurrentSchemaInvalidActiveInterfaceReferenceIsCorrupt(
        string damage)
    {
        var persistence = MonitoringPersistenceWithActiveInterfaceCondition();
        var state = JsonNode.Parse(persistence.Content!)!.AsObject();
        DamageActiveEvent(state, damage);
        persistence.Content = state.ToJsonString();

        AssertMonitoringStateIsQuarantinedAsCorrupt(persistence);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void MonitoringStore_SaveFailureTransitionsToFailClosedStorageState(
        Type exceptionType)
    {
        var expected = (Exception)Activator.CreateInstance(
            exceptionType,
            "simulated write failure")!;
        var persistence = new TestMonitoringPersistence
        {
            WriteException = expected
        };
        var store = new ViewerMonitoringStore("monitor.json", persistence);
        var device = Profile();

        var thrown = Record.Exception(
            () => store.RecordFailure(device, "TCP_TIMEOUT"));

        Assert.Same(expected, thrown);
        Assert.Null(store.GetActiveFailureCode(device.Id));
        Assert.Empty(store.LoadEvents());
        Assert.Equal(
            ViewerMonitoringLoadStatus.StorageUnavailable,
            store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_MONITOR_STATE_UNAVAILABLE", store.LoadErrorCode);
        Assert.Equal(1, persistence.WriteCount);

        persistence.WriteException = null;
        var blocked = Assert.Throws<InvalidOperationException>(
            () => store.RecordFailure(device, "TCP_TIMEOUT"));
        Assert.Equal("VIEWER_MONITOR_STATE_UNAVAILABLE", blocked.Message);
        Assert.Equal(1, persistence.WriteCount);

        var restarted = new ViewerMonitoringStore("monitor.json", persistence);
        var created = Assert.Single(
            restarted.RecordFailure(device, "TCP_TIMEOUT"));
        Assert.Equal(1, created.Sequence);
    }

    [Fact]
    public void MonitoringStore_SaveFailureDoesNotAdvanceOutputBaseline()
    {
        var persistence = new TestMonitoringPersistence();
        var store = new ViewerMonitoringStore("monitor.json", persistence);
        var device = Profile();
        Assert.Empty(store.RecordOutput(
            device,
            "show port status",
            PortStatus(("1", "Up"))));
        var persistedBaseline = persistence.Content;

        persistence.WriteException = new IOException("simulated write failure");
        Assert.Throws<IOException>(
            () => store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));

        Assert.Equal(persistedBaseline, persistence.Content);
        Assert.Empty(store.LoadEvents());
        Assert.Equal(0, store.GetActiveInterfaceConditionCount(device.Id));
        Assert.Equal(
            ViewerMonitoringLoadStatus.StorageUnavailable,
            store.LastLoadStatus);

        persistence.WriteException = null;
        Assert.Throws<InvalidOperationException>(
            () => store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
        var restarted = new ViewerMonitoringStore("monitor.json", persistence);
        var created = Assert.Single(
            restarted.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
        Assert.Equal(1, created.Sequence);
    }

    [Fact]
    public void MonitoringStore_ResetDeviceCollectionState_IsAtomicAndPreservesClosedHistory()
    {
        var persistence = new TestMonitoringPersistence();
        var store = new ViewerMonitoringStore("monitor.json", persistence);
        var device = Profile();
        Assert.Empty(store.RecordOutput(
            device,
            "show port status",
            PortStatus(("1", "Up"))));
        Assert.Single(store.RecordOutput(
            device,
            "show port status",
            PortStatus(("1", "Down"))));
        store.RecordCapability(
            device.Id,
            new CollectorCapabilityDto("interface_status", true, "Ready"));
        Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));
        var persistedBeforeReset = persistence.Content;

        persistence.WriteException = new IOException("simulated reset write failure");
        Assert.Throws<IOException>(() => store.ResetDeviceCollectionState(device.Id));

        Assert.Equal(persistedBeforeReset, persistence.Content);
        Assert.Single(store.LoadCapabilities(device.Id));
        Assert.Equal("TCP_TIMEOUT", store.GetActiveFailureCode(device.Id));
        Assert.Equal(1, store.GetActiveInterfaceConditionCount(device.Id));
        Assert.All(store.LoadEvents(), item => Assert.False(item.Recovered));
        Assert.Equal(
            ViewerMonitoringLoadStatus.StorageUnavailable,
            store.LastLoadStatus);

        persistence.WriteException = null;
        Assert.Throws<InvalidOperationException>(
            () => store.ResetDeviceCollectionState(device.Id));
        var restarted = new ViewerMonitoringStore("monitor.json", persistence);
        var closed = restarted.ResetDeviceCollectionState(device.Id);

        Assert.Equal(2, closed.Count);
        Assert.All(closed, item =>
        {
            Assert.True(item.Acknowledged);
            Assert.True(item.Recovered);
            Assert.False(item.IsActiveCondition);
            Assert.NotNull(item.RecoveredAt);
            Assert.Contains("이전 상태 추적을 종료", item.Detail, StringComparison.Ordinal);
        });
        Assert.Empty(restarted.LoadCapabilities(device.Id));
        Assert.Null(restarted.GetActiveFailureCode(device.Id));
        Assert.Equal(0, restarted.GetActiveInterfaceConditionCount(device.Id));
        Assert.Equal(2, restarted.LoadEvents().Count);
        Assert.All(restarted.LoadEvents(), item => Assert.True(item.Recovered));

        var persisted = JsonNode.Parse(persistence.Content!)!.AsObject();
        Assert.Empty(persisted["Baselines"]!.AsObject());
        Assert.Empty(persisted["Capabilities"]!.AsObject());
        Assert.Empty(persisted["ActiveFailures"]!.AsObject());
        Assert.Empty(persisted["ActiveInterfaceConditions"]!.AsObject());

        var writeCount = persistence.WriteCount;
        Assert.Empty(restarted.ResetDeviceCollectionState(device.Id));
        Assert.Equal(writeCount, persistence.WriteCount);
    }

    [Fact]
    public void MonitoringStore_SyslogDiffHandlesSubsetDuplicatesReorderingAndAdditions()
    {
        var folder = TemporaryFolder();
        try
        {
            var device = Profile();

            var subsetStore = Store("subset");
            Assert.Empty(subsetStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Empty(subsetStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((2, "line-b"))));

            var duplicateStore = Store("duplicate");
            Assert.Empty(duplicateStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            var duplicate = duplicateStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"), (2, "line-b")));
            var duplicateEvent = Assert.Single(duplicate);
            Assert.Equal("새 로그", duplicateEvent.Kind);
            Assert.Equal("새 시스템 로그 1건", duplicateEvent.Title);

            var reorderStore = Store("reorder");
            Assert.Empty(reorderStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"), (3, "line-c"))));
            Assert.Empty(reorderStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((3, "line-c"), (1, "line-a"), (2, "line-b"))));

            var additionStore = Store("addition");
            Assert.Empty(additionStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            var additions = additionStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((3, "line-c"), (2, "line-b"), (4, "line-d"), (1, "line-a")));
            var additionEvent = Assert.Single(additions);
            Assert.Equal("새 로그", additionEvent.Kind);
            Assert.Equal("새 시스템 로그 2건", additionEvent.Title);

            var fallbackStore = Store("show-log-ram");
            Assert.Empty(fallbackStore.RecordOutput(
                device,
                "show log ram",
                Syslog((1, "line-a"))));
            var fallbackAddition = Assert.Single(fallbackStore.RecordOutput(
                device,
                "show log ram",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Equal("새 시스템 로그 1건", fallbackAddition.Title);

            ViewerMonitoringStore Store(string name) =>
                new(Path.Combine(folder, $"monitor-{name}.json"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_SyslogResetOrRotationReportsStateWithoutFalseNewLog()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            Assert.Empty(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            var rotation = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((3, "line-c"), (4, "line-d"))));
            Assert.Equal("로그 상태", rotation.Kind);
            Assert.Equal("로그 버퍼 순환 또는 초기화 감지", rotation.Title);

            var afterRotation = store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((5, "line-e"), (3, "line-c"), (4, "line-d")));
            var item = Assert.Single(afterRotation);
            Assert.Equal("새 시스템 로그 1건", item.Title);

            var cleared = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                "No syslog entries."));
            Assert.Equal("로그 상태", cleared.Kind);
            Assert.Empty(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((6, "line-f"))));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AgentV4Mapper_RequiresIdentityAndMapsRawCommandResults()
    {
        var identity = AgentContractMapper.MapIdentityV4("""
        {
          "apiVersion":4,
          "agentId":"agent-a",
          "instanceId":"instance-a",
          "certificatePublicKeySha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
          "protocol":"https",
          "maxCommandsPerRequest":8,
          "maxOutputBytes":65536
        }
        """);
        var result = AgentContractMapper.MapTelnetExecutionResultV4("""
        {
          "apiVersion":4,
          "requestId":"request-a",
          "success":true,
          "privilege":"privileged",
          "promptTerminator":"#",
          "startedUtc":"2026-07-23T01:00:00Z",
          "completedUtc":"2026-07-23T01:00:01Z",
          "durationMs":1000,
          "sessionCount":1,
          "reconnectCount":0,
          "commands":[{
            "command":"show running-config",
            "output":"raw-result",
            "truncated":false,
            "collectedUtc":"2026-07-23T01:00:01Z"
          }]
        }
        """);

        Assert.Equal(4, identity.ApiVersion);
        Assert.Equal("agent-a", identity.AgentId);
        Assert.Equal("raw-result", Assert.Single(result.Commands).Output);
        Assert.Equal("#", result.PromptTerminator);
    }

    [Fact]
    public void CertificateTrust_IsAutomaticAndBlocksChangedAgentKey()
    {
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var first = CreateCertificate(firstKey, "CN=agent-a");
        using var second = CreateCertificate(secondKey, "CN=agent-a");
        var settings = new ViewerSettings { AgentUri = "https://agent-a:18443" };
        var initial = new CertificatePinValidator(settings);

        Assert.True(initial.Validate(new HttpRequestMessage(), first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        var firstPin = CertificatePinValidator.GetSpkiSha256(first);
        Assert.True(initial.CompleteTrust(firstPin));
        Assert.True(settings.TryGetAgentTrustPin(out var stored));
        Assert.Equal(firstPin, stored);

        var changed = new CertificatePinValidator(settings);
        Assert.False(changed.Validate(new HttpRequestMessage(), second, null, SslPolicyErrors.None));
        Assert.True(changed.IdentityChanged);
    }

    [Fact]
    public void CurrentUserDpapi_RoundTripsWithoutReturningPlainText()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new CurrentUserSecretProtector();
        const string secret = "do-not-store-plain";

        var encrypted = protector.Protect(secret);

        Assert.DoesNotContain(secret, encrypted, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(encrypted));
    }

    [Fact]
    public async Task ManualQuery_SendsTargetAndCredentialsOnEveryRequestAndKeepsRawOutputInMemoryOnly()
    {
        var folder = TemporaryFolder();
        try
        {
            var devicePath = Path.Combine(folder, "devices.json");
            var monitorPath = Path.Combine(folder, "monitor.json");
            var settingsPath = Path.Combine(folder, "settings.json");
            var devices = new ManagedDeviceStore(devicePath, new TestProtector());
            var draft = Draft("login-secret", "enable-secret");
            draft.ConnectionVerified = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            draft.LastConnectionTestCode = "OK";
            var saved = devices.Save(draft);
            var client = new StatelessFakeClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(settingsPath),
                new StatelessFactory(client),
                deviceStore: devices,
                monitoringStore: new ViewerMonitoringStore(monitorPath));
            try
            {
                await viewModel.InitializeAsync();
                viewModel.SelectedDevice = Assert.Single(viewModel.Devices);
                viewModel.ReadOnlyQueryCommand = "show running-config";

                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                await WaitUntilAsync(() => !viewModel.IsReadOnlyQueryRunning && client.LastRequest is not null);

                var request = Assert.IsType<TelnetExecuteRequestDto>(client.LastRequest);
                Assert.Equal(saved.Host, request.Host);
                Assert.Equal("operator", request.Username);
                Assert.Equal("login-secret", request.Password);
                Assert.Equal("enable-secret", request.EnablePassword);
                Assert.Equal(["show running-config"], request.Commands);
                Assert.Equal("sensitive raw output", viewModel.ReadOnlyQueryOutput);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
            Assert.DoesNotContain("sensitive raw output", File.ReadAllText(devicePath), StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive raw output", File.ReadAllText(monitorPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task InitialAgentFailure_StillLoadsViewerDevicesAndManualRefreshRecovers()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = new ManagedDeviceStore(Path.Combine(folder, "devices.json"), new TestProtector());
            devices.Save(Draft("login-secret", null));
            var client = new RecoveringStatelessClient(startFailures: 1);
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new RecoveringStatelessFactory(client),
                deviceStore: devices);
            try
            {
                await viewModel.InitializeAsync();

                Assert.Single(viewModel.Devices);
                Assert.True(viewModel.ReadOnlyQueriesEnabled);
                Assert.Equal(AgentConnectionState.Offline, viewModel.HttpConnectionState);
                Assert.Null(viewModel.LastSuccessfulReceiptAt);

                viewModel.RefreshCommand.Execute(null);
                await WaitUntilAsync(() =>
                    !viewModel.IsBusy
                    && client.SuccessfulStarts == 1
                    && viewModel.HttpConnectionState == AgentConnectionState.Demo);

                Assert.Single(viewModel.Devices);
                Assert.NotNull(viewModel.LastSuccessfulReceiptAt);
                Assert.Contains("연결 확인 완료", viewModel.OperationMessage, StringComparison.Ordinal);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task AuthenticationFailure_BlocksFurtherMonitoringEvenWhenPersistenceFails()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = new ThrowingConnectionTestStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector());
            var draft = Draft("login-secret", null);
            draft.ConnectionVerified = true;
            draft.MonitoringEnabled = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            draft.LastConnectionTestCode = "OK";
            var saved = devices.Save(draft);
            var client = new AuthenticationFailureClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new AuthenticationFailureFactory(client),
                deviceStore: devices,
                monitoringStore: new ViewerMonitoringStore(Path.Combine(folder, "monitor.json")));
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    viewModel.IsMonitoringCredentialBlocked(saved.Id)
                    && viewModel.OperationMessage.Contains("설정 파일 저장은 실패", StringComparison.Ordinal));

                Assert.Equal(1, client.ExecuteCount);
                Assert.True(Assert.Single(devices.Load()).MonitoringEnabled);
                Assert.Contains("설정 파일 저장은 실패", viewModel.OperationMessage, StringComparison.Ordinal);

                await viewModel.RunMonitoringCycleAsync();

                Assert.Equal(1, client.ExecuteCount);

                var verified = devices.CreateEditDraft(saved.Id);
                verified.ConnectionVerified = true;
                verified.MonitoringEnabled = true;
                verified.LastConnectionTestUtc = DateTimeOffset.UtcNow.AddSeconds(1);
                verified.LastConnectionTestCode = "OK";
                viewModel.SaveManagedDevice(verified);

                Assert.False(viewModel.IsMonitoringCredentialBlocked(saved.Id));
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task AuthenticationFailure_BlocksBeforeMonitoringStateWrite()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = new ThrowingConnectionTestStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector());
            var draft = Draft("login-secret", null);
            draft.ConnectionVerified = true;
            draft.MonitoringEnabled = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            draft.LastConnectionTestCode = "OK";
            var saved = devices.Save(draft);
            var monitoringPersistence = new TestMonitoringPersistence
            {
                WriteExceptionAfterSuccessfulWrites =
                    new IOException("simulated monitoring state write failure")
            };
            var client = new AuthenticationFailureClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new AuthenticationFailureFactory(client),
                deviceStore: devices,
                monitoringStore: new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json"),
                    monitoringPersistence));
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    viewModel.IsMonitoringCredentialBlocked(saved.Id)
                    && client.ExecuteCount == 1);
                await WaitUntilAsync(() =>
                    viewModel.OperationMessage.Contains(
                        "VIEWER_MONITOR_STATE_UNAVAILABLE",
                        StringComparison.Ordinal));

                Assert.Equal(1, client.ExecuteCount);
                Assert.True(Assert.Single(devices.Load()).MonitoringEnabled);

                await viewModel.RunMonitoringCycleSafelyAsync(CancellationToken.None);

                Assert.Contains(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);

                monitoringPersistence.WriteExceptionAfterSuccessfulWrites = null;
                await viewModel.RunMonitoringCycleAsync();

                Assert.Equal(1, client.ExecuteCount);
            }
            finally
            {
                monitoringPersistence.WriteExceptionAfterSuccessfulWrites = null;
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static ManagedDeviceDraft Draft(string password, string? enablePassword) => new()
    {
        DisplayName = "ACCESS-SW-01",
        Model = "IES4224GP",
        Host = "192.0.2.10",
        Username = "operator",
        Password = password,
        EnablePassword = enablePassword ?? string.Empty
    };

    private static void AssertStoreOperationBlocked(
        string expectedErrorCode,
        Action operation)
    {
        var exception = Assert.ThrowsAny<Exception>(operation);
        Assert.Equal(expectedErrorCode, exception.Message);
    }

    private static void AssertAllStoreOperationsBlocked(
        ManagedDeviceStore store,
        string expectedErrorCode)
    {
        AssertStoreOperationBlocked(expectedErrorCode, () => store.Save(Draft("pw", null)));
        AssertStoreOperationBlocked(expectedErrorCode, () => store.Delete("device"));
        AssertStoreOperationBlocked(
            expectedErrorCode,
            () => store.SetMonitoring("device", enabled: true));
        AssertStoreOperationBlocked(
            expectedErrorCode,
            () => store.MarkConnectionTest("device", success: true, "OK"));
        AssertStoreOperationBlocked(expectedErrorCode, () => store.GetSecrets("device"));
        AssertStoreOperationBlocked(
            expectedErrorCode,
            () => store.ResolveDraftForOperation(new ManagedDeviceDraft { Id = "device" }));
        AssertStoreOperationBlocked(
            expectedErrorCode,
            () => store.ResolveDraftForOperation(new ManagedDeviceDraft()));
        AssertStoreOperationBlocked(expectedErrorCode, () => store.CreateEditDraft("device"));
    }

    private static ManagedDeviceProfile Profile() => new()
    {
        Id = "sw-01",
        DisplayName = "ACCESS-SW-01",
        Model = "IES4224GP",
        Host = "192.0.2.10",
        Port = 23,
        ProtectedUsername = "protected",
        ProtectedPassword = "protected",
        ConnectionVerified = true,
        MonitoringEnabled = true
    };

    private static string PortStatus(params (string PortId, string Link)[] ports)
    {
        var lines = new List<string> { "Port Admin Link Speed Duplex" };
        lines.AddRange(ports.Select(port =>
            $"{port.PortId} Enabled {port.Link} 1000M Full"));
        return string.Join("\r\n", lines);
    }

    private static string Syslog(params (int Sequence, string Message)[] entries) =>
        string.Join(
            "\r\n",
            entries.Select(entry =>
                $"[{entry.Sequence}] 00:00:{entry.Sequence:00} 2026-07-23\r\n" +
                $"\"{entry.Message}\"\r\n" +
                "level: 6, module: 6, function: 1, and event no.: 1"));

    private static string CompleteMonitoringStateJson() => """
    {
      "SchemaVersion": 3,
      "NextSequence": 0,
      "Baselines": {},
      "ActiveFailures": {},
      "ActiveInterfaceConditions": {},
      "Capabilities": {},
      "Events": []
    }
    """;

    private static TestMonitoringPersistence MonitoringPersistenceWithClosedFailure()
    {
        var persistence = new TestMonitoringPersistence();
        var source = new ViewerMonitoringStore("monitor.json", persistence);
        var device = Profile();
        Assert.Single(source.RecordFailure(device, "TCP_TIMEOUT"));
        Assert.Equal(2, source.RecordSuccess(device).Count);
        return persistence;
    }

    private static TestMonitoringPersistence MonitoringPersistenceWithActiveFailure()
    {
        var persistence = new TestMonitoringPersistence();
        var source = new ViewerMonitoringStore("monitor.json", persistence);
        Assert.Single(source.RecordFailure(Profile(), "TCP_TIMEOUT"));
        return persistence;
    }

    private static TestMonitoringPersistence MonitoringPersistenceWithActiveInterfaceCondition()
    {
        var persistence = new TestMonitoringPersistence();
        var source = new ViewerMonitoringStore("monitor.json", persistence);
        var device = Profile();
        Assert.Empty(source.RecordOutput(
            device,
            "show port status",
            PortStatus(("1", "Up"))));
        Assert.Single(source.RecordOutput(
            device,
            "show port status",
            PortStatus(("1", "Down"))));
        return persistence;
    }

    private static void DamageActiveEvent(JsonObject state, string damage)
    {
        var activeEvent = state["Events"]![0]!.AsObject();
        switch (damage)
        {
            case "Recovered":
                activeEvent["Recovered"] = true;
                activeEvent["RecoveredAt"] = "2026-07-28T00:01:00+00:00";
                break;
            case "DeviceId":
                activeEvent["DeviceId"] = "different-device";
                break;
            case "ConditionKey":
                activeEvent["ConditionKey"] = "different-condition";
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(damage),
                    damage,
                    null);
        }
    }

    private static void AssertMonitoringStateIsQuarantinedAsCorrupt(
        TestMonitoringPersistence persistence)
    {
        var writeCount = persistence.WriteCount;
        var store = new ViewerMonitoringStore("monitor.json", persistence);

        Assert.Empty(store.LoadEvents());
        Assert.Equal(ViewerMonitoringLoadStatus.Corrupt, store.LastLoadStatus);
        Assert.False(store.IsOperational);
        Assert.Equal("VIEWER_MONITOR_STATE_CORRUPT", store.LoadErrorCode);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Null(persistence.Content);
        Assert.Equal(writeCount, persistence.WriteCount);
    }

    private static string TemporaryFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerManaged", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static X509Certificate2 CreateCertificate(ECDsa key, string subject)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TestProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("protected:" + plainText));

        public string Unprotect(string protectedText)
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            return decoded["protected:".Length..];
        }
    }

    private sealed class ThrowingProtectProtector(Exception exception)
        : IViewerSecretProtector
    {
        public string Protect(string plainText) => throw exception;

        public string Unprotect(string protectedText)
        {
            var decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedText));
            return decoded["protected:".Length..];
        }
    }

    private sealed class TestManagedDevicePersistence : IManagedDevicePersistence
    {
        public string? Content { get; set; }
        public Exception? ReadException { get; set; }
        public Exception? WriteException { get; set; }
        public Exception? QuarantineException { get; init; }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int QuarantineCount { get; private set; }

        public string? ReadIfExists(string path)
        {
            ReadCount++;
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

    private sealed class TestMonitoringPersistence : IViewerMonitoringPersistence
    {
        public string? Content { get; set; }
        public Exception? ReadException { get; init; }
        public Exception? WriteException { get; set; }
        public Exception? WriteExceptionAfterSuccessfulWrites { get; set; }
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
            if (WriteExceptionAfterSuccessfulWrites is not null && WriteCount > 1)
            {
                throw WriteExceptionAfterSuccessfulWrites;
            }
            Content = content;
        }

        public void Quarantine(string path, string destination)
        {
            QuarantineCount++;
            if (QuarantineException is not null) throw QuarantineException;
            Content = null;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class StatelessFactory(StatelessFakeClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class QueueClientFactory(params IAgentClient[] clients) : IAgentClientFactory
    {
        private readonly Queue<IAgentClient> _clients = new(clients);

        public IAgentClient Create(ViewerSettings settings) => _clients.Dequeue();
    }

    private sealed class LegacyFakeClient(AgentSnapshotDto snapshot) : IAgentClient
    {
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);

        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);

        public Task<EventChangePageDto> GetEventChangesAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));

        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
            string deviceId,
            string commandId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResultDto(false, "not used"));

        public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
            string deviceId,
            string command,
            CancellationToken cancellationToken) =>
            Task.FromException<ReadOnlyQueryResultDto>(new NotSupportedException());

        public Task<bool> AcknowledgeAsync(
            string eventId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StatelessFakeClient : IAgentClient
    {
        public TelnetExecuteRequestDto? LastRequest { get; private set; }
        public bool SupportsStatelessV4 => true;
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Demo);
            return Task.CompletedTask;
        }
        public Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentIdentityDto(4, "fake", "fake-instance", new string('A', 64), "https", 8, 65_536));
        public Task<TelnetExecutionResultDto> TestTelnetAsync(TelnetTargetDto target, CancellationToken cancellationToken) =>
            Task.FromResult(Result(target.RequestId, []));
        public Task<TelnetExecutionResultDto> ExecuteTelnetAsync(TelnetExecuteRequestDto request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result(request.RequestId,
            [
                new TelnetCommandOutputDto(request.Commands[0], "sensitive raw output", false, DateTimeOffset.UtcNow)
            ]));
        }
        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<AgentSnapshotDto>(new NotSupportedException());
        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);
        public Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));
        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResultDto(false, "not used"));
        public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(string deviceId, string command, CancellationToken cancellationToken) =>
            Task.FromException<ReadOnlyQueryResultDto>(new NotSupportedException());
        public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TelnetExecutionResultDto Result(
            string requestId,
            IReadOnlyList<TelnetCommandOutputDto> commands)
        {
            var now = DateTimeOffset.UtcNow;
            return new TelnetExecutionResultDto(4, requestId, true, "privileged", "#", now, now, 1, commands);
        }
    }

    private sealed class ThrowingConnectionTestStore(string path, IViewerSecretProtector protector)
        : ManagedDeviceStore(path, protector)
    {
        public override ManagedDeviceProfile MarkConnectionTest(string id, bool success, string code) =>
            throw new IOException("simulated write failure");
    }

    private sealed class RecoveringStatelessFactory(RecoveringStatelessClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class RecoveringStatelessClient(int startFailures) : StatelessClientBase
    {
        private int _remainingStartFailures = startFailures;
        public int SuccessfulStarts { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _remainingStartFailures) >= 0)
            {
                RaiseConnectionState(AgentConnectionState.Offline);
                throw new AgentClientException("AGENT_UNREACHABLE", AgentConnectionState.Offline);
            }
            SuccessfulStarts++;
            RaiseConnectionState(AgentConnectionState.Demo);
            return Task.CompletedTask;
        }
    }

    private sealed class AuthenticationFailureFactory(AuthenticationFailureClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class AuthenticationFailureClient : StatelessClientBase
    {
        public int ExecuteCount { get; private set; }

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            throw new AgentClientException("AUTH_FAILED", AgentConnectionState.Stale);
        }
    }

    private abstract class StatelessClientBase : IAgentClient
    {
        public bool SupportsStatelessV4 => true;
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

        public virtual Task StartAsync(CancellationToken cancellationToken)
        {
            RaiseConnectionState(AgentConnectionState.Demo);
            return Task.CompletedTask;
        }

        protected void RaiseConnectionState(AgentConnectionState state) =>
            ConnectionStateChanged?.Invoke(this, state);

        public Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentIdentityDto(4, "fake", "fake-instance", new string('A', 64), "https", 8, 65_536));

        public Task<TelnetExecutionResultDto> TestTelnetAsync(
            TelnetTargetDto target,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result(target.RequestId, []));

        public virtual Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result(request.RequestId,
            [
                new TelnetCommandOutputDto(request.Commands[0], "output", false, DateTimeOffset.UtcNow)
            ]));

        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<AgentSnapshotDto>(new NotSupportedException());

        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);

        public Task<EventChangePageDto> GetEventChangesAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));

        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
            string deviceId,
            string commandId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResultDto(false, "not used"));

        public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
            string deviceId,
            string command,
            CancellationToken cancellationToken) =>
            Task.FromException<ReadOnlyQueryResultDto>(new NotSupportedException());

        public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TelnetExecutionResultDto Result(
            string requestId,
            IReadOnlyList<TelnetCommandOutputDto> commands)
        {
            var now = DateTimeOffset.UtcNow;
            return new TelnetExecutionResultDto(4, requestId, true, "privileged", "#", now, now, 1, commands);
        }
    }
}
