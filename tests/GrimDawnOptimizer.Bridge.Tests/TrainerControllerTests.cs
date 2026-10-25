using System.Text.Json;
using GrimDawnOptimizer.Bridge.Trainer;
using Xunit;

namespace GrimDawnOptimizer.Bridge.Tests;

public sealed class TrainerControllerTests
{
    [Fact]
    public async Task DisconnectedCommandsHaveIndependentFiniteSnapshotsAndValidateBeforeMutation()
    {
        using var fixture = new Fixture();
        using var controller = fixture.Controller();
        var before = controller.Snapshot();
        Assert.Equal("disconnected", before.Status);
        Assert.False(before.Connected);
        Assert.Null(before.Health);
        Assert.Equal(9, before.Features.Count);
        Assert.All(before.Features, item => Assert.False(item.Requested || item.Applied || item.Pending));
        await controller.ExecuteAsync("trainerSetMovementMultiplier", Payload(new { value = 4 }));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ExecuteAsync("trainerSetMovementMultiplier", Payload(new { value = 3 })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "life", enabled = true })));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "retiredArmor", enabled = true })));
        var after = controller.Snapshot();
        Assert.Equal(4, after.MovementMultiplier);
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.True(after.Revision > before.Revision);
        Assert.Equal(0, fixture.CreatedSessions);
        Assert.False(File.Exists(fixture.PreferencePath));
        var json = JsonSerializer.SerializeToElement(after, Program.JsonOptions);
        Assert.False(json.TryGetProperty("results", out _));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("health").ValueKind);
    }

    [Theory]
    [InlineData("trainerDisableAll")]
    [InlineData("trainerSetMovementMultiplier")]
    public async Task SuccessfulOfflineCommandsClearPreviousErrorStatus(string command)
    {
        using var fixture = new Fixture();
        using var controller = fixture.Controller();
        controller.ReportError(new ArgumentException("无效路径"));
        Assert.Equal("error", controller.Snapshot().Status);
        await controller.ExecuteAsync(command, Payload(new { value = 4 }));
        var state = controller.Snapshot();
        Assert.Equal("disconnected", state.Status);
        Assert.Null(state.Error);
        Assert.DoesNotContain("操作未完成", state.StatusText);
    }

    [Fact]
    public async Task RequestedAndAppliedAreSeparateAndHotkeysShareAvailabilityRules()
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var session = new FakeSession();
        using var controller = fixture.Controller(() => session);
        await controller.ExecuteAsync("trainerConnect", default);
        Assert.True(controller.Snapshot().Available);
        Assert.Equal(1, fixture.Hotkeys.Starts);
        Assert.Equal(0, session.Requested);
        await controller.ExecuteAsync("trainerSetMovementMultiplier", Payload(new { value = 16 }));
        Assert.Equal(16, session.Multiplier);
        Assert.Equal(0, session.Requested);
        await controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "life", enabled = true }));
        var requested = controller.Snapshot().Features.Single(x => x.Id == "life");
        Assert.True(requested.Requested && requested.Pending);
        Assert.False(requested.Applied);
        session.Runtime = session.Runtime with { Applied = 1 };
        controller.Poll();
        Assert.True(controller.Snapshot().Features[0].Applied);
        Assert.False(controller.Snapshot().Features[0].Pending);
        await controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "life", enabled = false }));
        var closing = controller.Snapshot().Features[0];
        Assert.False(closing.Requested);
        Assert.True(closing.Applied && closing.Pending);
        session.Runtime = session.Runtime with { Applied = 0 };
        controller.Poll();
        Assert.False(controller.Snapshot().Features[0].Pending);
        fixture.Hotkeys.Trigger(1);
        fixture.Hotkeys.Trigger(2);
        Assert.Equal(3, session.Requested);
        fixture.Hotkeys.Trigger(12);
        Assert.Equal(0, session.Requested);
        await controller.ExecuteAsync("trainerDisconnect", default);
        fixture.Hotkeys.Trigger(1); // A queued shortcut from the old generation.
        Assert.Equal(0, session.Requested);
        Assert.True(session.Disposals > 0);
        Assert.Equal(1, fixture.Hotkeys.Stops);
        Assert.False(controller.Snapshot().Connected);
    }

    [Theory]
    [InlineData(3, "multiplayer")]
    [InlineData(1, "waiting")]
    [InlineData(2, "stale")]
    [InlineData(-1, "fault")]
    public async Task UnavailableSessionClearsRequestsAndDoesNotResumeThem(int nativeStatus, string status)
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var session = new FakeSession();
        using var controller = fixture.Controller(() => session);
        await controller.ExecuteAsync("trainerConnect", default);
        await controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "moveSpeed", enabled = true }));
        session.Runtime = session.Runtime with { Status = nativeStatus, Applied = 8 };
        if (status == "stale") fixture.Now = 5000;
        controller.Poll();
        var paused = controller.Snapshot();
        Assert.Equal(status, paused.Status);
        Assert.False(paused.Available);
        Assert.Equal(0, session.Requested);
        Assert.All(paused.Features, feature => Assert.False(feature.Requested || feature.Applied));
        Assert.Null(paused.Health);
        fixture.Hotkeys.Trigger(4);
        Assert.Equal(0, session.Requested);
        session.Runtime = session.Runtime with { Status = 2, Applied = 0, Tick = fixture.Now };
        controller.Poll();
        Assert.True(controller.Snapshot().Available);
        Assert.All(controller.Snapshot().Features, feature => Assert.False(feature.Requested));
    }

    [Fact]
    public async Task FaultAndGameExitDisableAllAndReleaseConnection()
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var session = new FakeSession();
        using var controller = fixture.Controller(() => session);
        await controller.ExecuteAsync("trainerConnect", default);
        await controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "life", enabled = true }));
        session.Runtime = session.Runtime with { Fault = 0xC0000005 };
        controller.Poll();
        Assert.Equal("fault", controller.Snapshot().Status);
        Assert.NotNull(controller.Snapshot().Error);
        Assert.Equal(0, session.Requested);
        fixture.Hotkeys.Trigger(12);
        Assert.Equal(0, session.Requested);
        session.Connected = false;
        controller.Poll();
        Assert.Equal("disconnected", controller.Snapshot().Status);
        Assert.True(session.Disposals > 0);
        Assert.Equal(1, fixture.Hotkeys.Stops);
    }

    [Theory]
    [InlineData("trainerDisconnect")]
    [InlineData("trainerDisableAll")]
    public async Task StopDuringConnectRejectsLateSuccess(string stop)
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var session = new FakeSession { PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var controller = fixture.Controller(() => session);
        var connect = controller.ExecuteAsync("trainerConnect", default);
        Assert.True(controller.Snapshot().Connecting);
        await controller.ExecuteAsync(stop, default);
        Assert.True(session.Cancellation.IsCancellationRequested);
        session.PendingConnect.SetResult(); // Fake deliberately ignores cancellation.
        await connect;
        Assert.False(controller.Snapshot().Connected || controller.Snapshot().Connecting || controller.Snapshot().Available);
        Assert.Equal("disconnected", controller.Snapshot().Status);
        Assert.Equal(0, fixture.Hotkeys.Starts);
        Assert.True(session.Disposals >= 1);
        Assert.Equal(0, session.Requested);
    }

    [Fact]
    public async Task OldConnectionCompletionCannotReplaceANewerSession()
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var old = new FakeSession { PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var next = new FakeSession();
        var sessions = new Queue<FakeSession>([old, next]);
        using var controller = fixture.Controller(() => sessions.Dequeue());
        var oldConnect = controller.ExecuteAsync("trainerConnect", default);
        await controller.ExecuteAsync("trainerDisconnect", default);
        await controller.ExecuteAsync("trainerConnect", default);
        await controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "mana", enabled = true }));
        old.PendingConnect.SetResult();
        await oldConnect;
        Assert.True(controller.Snapshot().Connected && controller.Snapshot().Available);
        Assert.Equal(2, next.Requested);
        Assert.Equal(0, next.Disposals);
        Assert.Equal(0, old.Requested);
        Assert.Equal(1, fixture.Hotkeys.Starts);
    }

    [Fact]
    public async Task DisposeDuringConnectCannotRegisterHotkeysOrReviveEffects()
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var session = new FakeSession { PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var controller = fixture.Controller(() => session);
        var connect = controller.ExecuteAsync("trainerConnect", default);
        controller.Dispose();
        session.PendingConnect.SetResult();
        await connect;
        Assert.Equal(0, session.Requested);
        Assert.False(session.Connected);
        Assert.True(fixture.Hotkeys.Disposed);
        Assert.Equal(0, fixture.Hotkeys.Starts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FailedOffOrDisposeStillReleasesHotkeysAndClearsControllerState(bool failOff, bool failDispose)
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var session = new FakeSession();
        using var controller = fixture.Controller(() => session);
        await controller.ExecuteAsync("trainerConnect", default);
        await controller.ExecuteAsync("trainerSetFeature", Payload(new { id = "life", enabled = true }));
        session.FailOff = failOff;
        session.FailDispose = failDispose;
        await Assert.ThrowsAsync<IOException>(() => controller.ExecuteAsync("trainerDisconnect", default));
        Assert.True(session.Disposals > 0);
        Assert.Equal(1, fixture.Hotkeys.Stops);
        Assert.False(controller.Snapshot().Connected || controller.Snapshot().Available);
        Assert.All(controller.Snapshot().Features, feature => Assert.False(feature.Requested || feature.Applied));
    }

    [Fact]
    public async Task PollFailuresRemainTrainerErrorsEvenWhenCleanupAlsoFails()
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        var session = new FakeSession();
        using var controller = fixture.Controller(() => session);
        await controller.ExecuteAsync("trainerConnect", default);
        session.FailRead = true;
        session.FailDispose = true;
        controller.Poll(); // Must not escape to the shared context's main error event.
        var state = controller.Snapshot();
        Assert.Equal("error", state.Status);
        Assert.False(state.Connected || state.Available);
        Assert.Contains("fake read failure", state.Error);
        Assert.Equal(1, fixture.Hotkeys.Stops);
    }

    [Fact]
    public async Task NonFiniteNativeTelemetrySerializesAsNullAndHotkeyFailureIsVisible()
    {
        using var fixture = new Fixture();
        fixture.SelectPath();
        fixture.Hotkeys.Warning = "Ctrl+Alt+F12 被占用";
        var session = new FakeSession { Runtime = Ready() with { Health = double.NaN, MaxHealth = float.PositiveInfinity, Mana = float.NegativeInfinity } };
        using var controller = fixture.Controller(() => session);
        await controller.ExecuteAsync("trainerConnect", default);
        var snapshot = controller.Snapshot();
        Assert.True(snapshot.Available);
        Assert.Equal(fixture.Hotkeys.Warning, snapshot.HotkeyWarning);
        Assert.Null(snapshot.Health);
        Assert.Null(snapshot.MaxHealth);
        Assert.Null(snapshot.Mana);
        Assert.Equal(250d, snapshot.MaxMana);
        JsonSerializer.Serialize(snapshot, Program.JsonOptions);
    }

    [Fact]
    public void NativeSnapshotDecoderChecksProtocolOffsetsAndMasksRetiredFlags()
    {
        var bytes = new byte[NativeTrainerSession.StateSize];
        Put(0, NativeTrainerSession.Magic); Put(4, NativeTrainerSession.ProtocolVersion);
        Put(12, TrainerFeatures.Mask | 32 | 64); Put(24, 2); Put(48, 1234UL);
        Put(56, 120.5); Put(64, 150f); Put(68, 50f); Put(72, 90f);
        Put(704, 15f); Put(708, 20f); Put(712, 321); Put(716, 123);
        var state = NativeTrainerSession.Decode(bytes);
        Assert.Equal(TrainerFeatures.Mask, state.Applied);
        Assert.Equal(120.5, state.Health);
        Assert.Equal(1234UL, state.Tick);
        Assert.Equal(15f, state.HealthPotionRecovery);
        Assert.Equal(123, state.ManaPotionCooldown);
        bytes[4] = 4;
        Assert.Throws<InvalidDataException>(() => NativeTrainerSession.Decode(bytes));
        Assert.Throws<InvalidDataException>(() => NativeTrainerSession.Decode(new byte[20]));
        void Put<T>(int offset, T value) where T : struct
        {
            var data = value switch
            {
                uint x => BitConverter.GetBytes(x), int x => BitConverter.GetBytes(x), ulong x => BitConverter.GetBytes(x),
                double x => BitConverter.GetBytes(x), float x => BitConverter.GetBytes(x), _ => throw new ArgumentException()
            };
            data.CopyTo(bytes, offset);
        }
    }

    [Fact]
    public void SettingsImportOnlyReadsLegacyAndRejectedPathsPreserveCurrentPreference()
    {
        using var fixture = new Fixture();
        var legacy = Path.Combine(fixture.Directory, "legacy.json");
        var legacyJson = JsonSerializer.Serialize(new { GameExecutable = fixture.Executable });
        File.WriteAllText(legacy, legacyJson);
        var imported = new TrainerSettings(fixture.PreferencePath, true, legacy);
        Assert.Equal(fixture.Executable, imported.Executable);
        Assert.False(File.Exists(fixture.PreferencePath));
        imported.SaveExecutable(fixture.Executable);
        Assert.Equal(legacyJson, File.ReadAllText(legacy));
        var saved = File.ReadAllText(fixture.PreferencePath);
        Assert.Throws<ArgumentException>(() => imported.SaveExecutable(Path.Combine(fixture.Directory, "wrong.exe")));
        Assert.Equal(saved, File.ReadAllText(fixture.PreferencePath));
        Assert.Equal(fixture.Executable, new TrainerSettings(fixture.PreferencePath, false).Executable);
        var x64 = Path.Combine(fixture.Directory, "x64", "Grim Dawn.exe");
        Assert.Equal(fixture.Directory, TrainerSettings.RootFor(x64));
        File.WriteAllText(fixture.PreferencePath, "{");
        Assert.NotNull(new TrainerSettings(fixture.PreferencePath, false).LoadWarning);
        Assert.Equal(TrainerFeatures.Mask, TrainerFeatures.All.Aggregate(0, (mask, feature) => mask | feature.Flag));
        Assert.Equal(9, TrainerFeatures.All.Select(x => x.Id).Distinct().Count());
    }

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);
    private static TrainerRuntimeSnapshot Ready() => new(2, 0, 0, 1000, 500, 1000, 100, 250, 200, 80, 0, 0);

    private sealed class FakeSession : ITrainerSession
    {
        public bool Connected { get; set; }
        public int ProcessId => 42;
        public string Architecture => "x64";
        public int Requested { get; private set; }
        public int Multiplier { get; private set; } = 2;
        public int Disposals { get; private set; }
        public bool FailOff { get; set; }
        public bool FailDispose { get; set; }
        public bool FailRead { get; set; }
        public TrainerRuntimeSnapshot Runtime { get; set; } = Ready();
        public TaskCompletionSource? PendingConnect { get; init; }
        public CancellationToken Cancellation { get; private set; }
        public async Task ConnectAsync(string executable, CancellationToken cancellationToken)
        {
            Cancellation = cancellationToken;
            if (PendingConnect is not null) await PendingConnect.Task;
            Connected = true;
        }
        public void SetFlags(int flags)
        {
            if (flags == 0 && FailOff) throw new IOException("fake off failure");
            Requested = flags;
        }
        public void SetMovementMultiplier(int value) => Multiplier = value;
        public TrainerRuntimeSnapshot? Read() => FailRead ? throw new IOException("fake read failure") : Runtime;
        public void Dispose()
        {
            ++Disposals; Connected = false; Requested = 0;
            if (FailDispose) throw new IOException("fake dispose failure");
        }
    }

    private sealed class FakeHotkeys : ITrainerHotkeys
    {
        private Action<int>? _callback;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }
        public string? Warning { get; set; }
        public string? Start(Action<int> onHotkey) { ++Starts; _callback = onHotkey; return Warning; }
        public void Trigger(int id) => _callback?.Invoke(id);
        public void Stop() => ++Stops;
        public void Dispose() => Disposed = true;
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "grim-trainer-test-" + Guid.NewGuid().ToString("N"));
        public string PreferencePath => Path.Combine(Directory, "preferences", "combat-settings.json");
        public string Executable => Path.Combine(Directory, "Grim Dawn.exe");
        public TrainerSettings Settings { get; }
        public FakeHotkeys Hotkeys { get; } = new();
        public ulong Now { get; set; } = 1000;
        public int CreatedSessions { get; private set; }
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            // Path fixtures only: these bytes are never executed or loaded.
            foreach (var name in new[] { "Grim Dawn.exe", "Game.dll", "Engine.dll" }) File.WriteAllBytes(Path.Combine(Directory, name), [0]);
            Settings = new TrainerSettings(PreferencePath, importLegacyDefault: false);
        }
        public void SelectPath() => Settings.SaveExecutable(Executable);
        public TrainerController Controller(Func<ITrainerSession>? factory = null) => new(Settings, action => action(),
            () => { ++CreatedSessions; return factory?.Invoke() ?? new FakeSession(); }, Hotkeys,
            _ => throw new InvalidOperationException("Tests must not launch a process."), () => Now);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
