using System.Text.Json;
using System.Text.Json.Serialization;
using Endnight.Extensions;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using Sons.Save;
using TheForest.Utils;

namespace Modstoke.Virginia;

internal sealed partial class VirginiaRomance
{
    private const string SaveOwner = "modstoke.virginia";
    private const string SaveName = "romance-v1";
    private static readonly JsonSerializerOptions SaveOptions = new() { MaxDepth = 8 };
    private IDisposable? _saveRegistration;
    private WorldRecordContext? _world;
    private IntPtr _saveManager;
    private int _transportMode = -1;
    private bool _loading, _offlineLoadComplete, _loadInvalid, _hasRecord;
    private Guid _offlineWorld;
    private SavedRomanceWorld? _pendingOffline;
    private string _saveProblem = "";

    private void StartPersistence()
    {
        _transportMode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        SetOfflineRegistration(_transportMode == 0);
        SaveLifecycle.Lifecycle += OnSaveLifecycle;
        GameRuntime.WorldChanged += OnWorldChanged;
    }

    private void StopPersistence()
    {
        SaveLifecycle.Lifecycle -= OnSaveLifecycle;
        GameRuntime.WorldChanged -= OnWorldChanged;
        SetOfflineRegistration(false);
    }

    private bool RefreshWorld()
    {
        var mode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        if (_transportMode != mode)
        {
            ResetWorld(preservePendingLoad: _loading || !_ready && _offlineLoadComplete);
            _transportMode = mode;
            if (!_loading) SetOfflineRegistration(mode == 0);
        }
        if (!_authority || mode == 2 || GameRuntime.IsTitleScene)
        {
            if (_ready) ResetWorld();
            _status = "Waiting for a host or single-player world.";
            return false;
        }
        if (_loading) { _status = "Waiting for world loading to finish."; return false; }
        if (!SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected
            || !manager || manager._loadingInProgress)
        {
            if (_ready) ResetWorld();
            _status = "Waiting for the world save manager.";
            return false;
        }
        if (_saveManager != IntPtr.Zero && _saveManager != manager.Pointer) ResetWorld();
        _saveManager = manager.Pointer;
        if (_loadInvalid) return false;
        if (mode == 1)
        {
            if (!WorldRecords.TryGetCurrent(out var world, out _))
            {
                if (_ready) ResetWorld();
                _status = "Waiting for this world's romance record.";
                return false;
            }
            if (!ReferenceEquals(_world, world))
            {
                ResetWorld();
                _saveManager = manager.Pointer;
                _world = world;
                var read = WorldRecords.ReadJson(world, SaveOwner, SaveName, out var json);
                if (read != WorldRecordStatus.NotFound
                    && (read != WorldRecordStatus.Success || !LoadRomance(json, world.WorldId)))
                    return InvalidSave();
                BeginAuthority();
            }
            return _ready;
        }
        if (_ready) return true;
        var transform = LocalPlayer.Transform;
        if (transform == null || transform.WasCollected || !transform)
        { _status = "Waiting for the local player."; return false; }
        if (GameSetupManager.GameLoadedFromSave() && !_offlineLoadComplete)
        { _status = "Waiting for this world's romance save record."; return false; }
        if (_pendingOffline != null)
        {
            if (!RestoreRomance(_pendingOffline, _pendingOffline.World)) return InvalidSave();
            _offlineWorld = _pendingOffline.World;
            _pendingOffline = null;
        }
        if (_offlineWorld == Guid.Empty) _offlineWorld = Guid.NewGuid();
        BeginAuthority();
        return true;
    }

    private void BeginAuthority()
    {
        _epoch = Guid.NewGuid();
        _ready = true;
        _status = "Mod progression is ready.";
    }

    private bool InvalidSave()
    {
        _loadInvalid = true;
        _ready = false;
        _epoch = Guid.Empty;
        CancelAll("Quiet time stopped because the save record is unavailable.");
        _status = "The romance save record is unavailable or invalid. Saved data was preserved.";
        PublishView();
        return false;
    }

    private void OnWorldChanged(WorldEvent value)
    {
        if (_disposed || !OnThread || _loading || value.IsInWorld || !GameRuntime.IsTitleScene) return;
        ResetWorld();
    }

    private void OnSaveLifecycle(SaveLifecycleEvent value)
    {
        if (_disposed || !OnThread) return;
        if (value.Operation == "Load" && value.Phase == "Before")
        {
            ResetWorld();
            _loading = true;
            SetOfflineRegistration(!value.SaveGameType.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase));
        }
        else if (value.Operation == "Load" && value.Phase == "After")
        {
            _loading = false;
            _offlineLoadComplete = value.Success == true
                && !value.SaveGameType.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase);
            if (value.Success != true) { _pendingOffline = null; InvalidSave(); }
        }
        else if (value.Operation == "Save" && value.Phase == "Before")
        {
            SetOfflineRegistration(!BoltNetwork.isRunning);
            StoreWorld();
        }
    }

    private void SetOfflineRegistration(bool active)
    {
        if (active) _saveRegistration ??= SaveRegistry.Register(new OfflineSave(this));
        else { _saveRegistration?.Dispose(); _saveRegistration = null; }
    }

    private void ResetWorld(bool preservePendingLoad = false)
    {
        var loading = preservePendingLoad && _loading;
        var complete = preservePendingLoad && _offlineLoadComplete;
        var pending = preservePendingLoad ? _pendingOffline : null;
        var invalid = preservePendingLoad && _loadInvalid;
        _ready = false;
        _epoch = Guid.Empty;
        _accounts.Clear();
        _actor = null;
        _actorAvailable = false;
        _world = null;
        _saveManager = IntPtr.Zero;
        _offlineWorld = Guid.Empty;
        _hasRecord = false;
        _saveProblem = "";
        _loading = loading;
        _offlineLoadComplete = complete;
        _pendingOffline = pending;
        _loadInvalid = invalid;
        _status = "Waiting for the current world.";
        _nextUpdate = 0;
        PublishView();
    }

    private SavedRomanceWorld CaptureRomance(Guid world)
    {
        var now = Now;
        var utc = DateTimeOffset.UtcNow;
        return new SavedRomanceWorld
        {
            World = world,
            Accounts = _accounts.Select(pair => new SavedRomanceAccount
            {
                Account = pair.Key,
                Bond = pair.Value.Bond,
                NotBeforeUtc = utc.AddSeconds(Math.Clamp(Math.Max(
                    pair.Value.NotBeforeMonotonic - now,
                    (pair.Value.NotBeforeUtc - utc).TotalSeconds), 0, CooldownSeconds))
            }).ToArray()
        };
    }

    private void StoreWorld()
    {
        if (!_hasRecord || !_ready || !_authority || _loading || _loadInvalid || _world == null) return;
        var result = WorldRecords.WriteJson(_world, SaveOwner, SaveName,
            JsonSerializer.Serialize(CaptureRomance(_world.WorldId), SaveOptions));
        _saveProblem = result == WorldRecordStatus.Success ? ""
            : "Mod progression is in memory but has not been accepted by the world save.";
        PublishView();
    }

    private bool LoadRomance(string json, Guid world)
    {
        if (json == null || json.Length > 32768) return false;
        try { return RestoreRomance(JsonSerializer.Deserialize<SavedRomanceWorld>(json, SaveOptions), world); }
        catch (JsonException) { return false; }
    }

    private bool RestoreRomance(SavedRomanceWorld? data, Guid world)
    {
        if (data == null || data.Version != 1 || world == Guid.Empty || data.World != world
            || data.Accounts == null || data.Accounts.Length > MaximumAccounts) return false;
        var accounts = new Dictionary<ulong, Progress>();
        var now = Now;
        var utc = DateTimeOffset.UtcNow;
        foreach (var saved in data.Accounts)
        {
            if (saved == null || saved.Account == 0 || saved.Bond is < 0 or > 100
                || saved.NotBeforeUtc.Offset != TimeSpan.Zero || saved.NotBeforeUtc == default
                || accounts.ContainsKey(saved.Account)) return false;
            // A fresh 60-second floor prevents clock changes/reloads bypassing a saved cooldown.
            // Arbitrarily distant UTC values cannot impose an unbounded wait.
            accounts.Add(saved.Account, new Progress
            {
                Bond = saved.Bond,
                NotBeforeUtc = utc.AddSeconds(CooldownSeconds),
                NotBeforeMonotonic = now + CooldownSeconds
            });
        }
        _accounts.Clear();
        foreach (var pair in accounts) _accounts.Add(pair.Key, pair.Value);
        _hasRecord = true;
        return true;
    }

    private sealed class OfflineSave(VirginiaRomance service) : ISaveable
    {
        public string ModId => SaveOwner;
        public string Name => SaveName + "-singleplayer";
        public bool IncludeInPlayerSave => false;
        public string Save()
        {
            if (!service.OnThread || BoltNetwork.isRunning)
                throw new InvalidOperationException("No single-player romance ledger is active.");
            if (!service._hasRecord)
            {
                // The registry checks registration identity after Save, before writing. Replacing
                // this registration skips the unused sidecar and retains its failed-load protection.
                var previous = service._saveRegistration;
                service._saveRegistration = SaveRegistry.Register(new OfflineSave(service));
                previous?.Dispose();
                return "";
            }
            if (!service._ready || service._loading || service._loadInvalid || service._offlineWorld == Guid.Empty)
                throw new InvalidOperationException("No single-player romance ledger is active.");
            return JsonSerializer.Serialize(service.CaptureRomance(service._offlineWorld), SaveOptions);
        }
        public void Load(string data)
        {
            if (!service.OnThread || BoltNetwork.isRunning
                || !service._loading && (service._ready || !service._offlineLoadComplete)) return;
            try
            {
                if (data == null || data.Length > 32768) throw new JsonException();
                service._pendingOffline = JsonSerializer.Deserialize<SavedRomanceWorld>(data, SaveOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException) { service.InvalidSave(); throw; }
        }
    }

    public sealed class SavedRomanceWorld
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public Guid World { get; set; }
        [JsonRequired] public SavedRomanceAccount[] Accounts { get; set; } = [];
    }

    public sealed class SavedRomanceAccount
    {
        [JsonRequired] public ulong Account { get; set; }
        [JsonRequired] public int Bond { get; set; }
        [JsonRequired] public DateTimeOffset NotBeforeUtc { get; set; }
    }
}
