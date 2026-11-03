using System.Diagnostics;
using Modstoke.Sons;
using Sons.Ai.Vail;
using Steamworks;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Virginia;

internal enum RomanceAction : byte { Greeting = 1, Appreciation = 2, QuietCompany = 3, StepBack = 4 }

internal record RomanceView(bool Available, bool HostEnabled, int Bond, int QuietSeconds, string Stage, string Feedback);

internal sealed partial class VirginiaRomance : IDisposable
{
    private const int MaximumAccounts = 128;
    private const double CooldownSeconds = 60;
    private const double UpdateIntervalSeconds = 0.25;
    private const double MaximumObservationGapSeconds = 1;
    private readonly Func<VirginiaSettings> _settings;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ulong, Progress> _accounts = new();
    private bool _disposed, _ready, _authority, _hostEnabled, _actorAvailable;
    private string _status = "Waiting for the current world.";
    private VailActor? _actor;
    private Guid _epoch;
    private double _nextUpdate;
    private VirginiaSettings? _lastSettings;
    private volatile ViewSnapshot _view = new(
        new(false, false, 0, 0, "Acquaintance (mod)", "Waiting for the current world."), new());

    internal VirginiaRomance(Func<VirginiaSettings> settings)
    {
        _settings = settings;
        StartPersistence();
    }

    internal bool IsAuthority => _authority && !_disposed;
    internal bool IsReady => _ready && IsAuthority;
    internal Guid Epoch => IsReady ? _epoch : Guid.Empty;
    internal bool RemoteQuietCompanySupported => false;
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal void Update() => Refresh(false);

    private void Refresh(bool force)
    {
        if (_disposed || !OnThread) return;
        var now = Now;
        var settings = _settings();
        if (!force && now < _nextUpdate && ReferenceEquals(settings, _lastSettings)) return;
        _nextUpdate = now + UpdateIntervalSeconds;
        _lastSettings = settings;
        try
        {
            _authority = !BoltNetwork.isRunning || BoltNetwork.isServer;
            _hostEnabled = settings.HostRomanceEnabled;
            if (!RefreshWorld()) { CancelAll(_status); _actorAvailable = false; return; }
            if (!_hostEnabled)
            {
                CancelAll("The host has disabled romance interactions.");
                _actor = null;
                _actorAvailable = false;
                _status = "The host has disabled romance interactions.";
                return;
            }
            _actorAvailable = VirginiaActorAccess.TryResolve(out var actor, out var reason);
            if (!_actorAvailable)
            {
                CancelAll(reason);
                _actor = null;
                _status = reason;
                return;
            }
            if (_actor != null && (_actor.WasCollected || !_actor || _actor.Pointer != actor.Pointer))
                CancelAll("Quiet time stopped because Virginia's actor changed.");
            _actor = actor;
            _status = "Mod progression is ready.";
            foreach (var pair in _accounts)
            {
                var entry = pair.Value;
                var pending = entry.Pending;
                if (pending == null) continue;
                if (!settings.QuietCompany || pending.Local && !settings.RomanceConsent)
                {
                    Cancel(entry, "Quiet time stopped because consent or the feature was disabled.");
                    continue;
                }
                if (pending.Actor.WasCollected || !pending.Actor || pending.Actor.Pointer != actor.Pointer
                    || now - pending.LastUpdate > MaximumObservationGapSeconds || Time.timeScale <= 0)
                {
                    Cancel(entry, "Quiet time stopped because continuous safe company could not be confirmed.");
                    continue;
                }
                if (!TrySafeCompany(pair.Key, pending.Binding, pending.Local, actor, true,
                        out var player, out var playerHealth, out reason)
                    || player == null || pending.Player == null
                    || pending.Player.WasCollected || !pending.Player || player.Pointer != pending.Player.Pointer
                    || pending.Stats == null || pending.Stats.WasCollected || !pending.Stats
                    || LocalPlayer.Stats == null || LocalPlayer.Stats.Pointer != pending.Stats.Pointer
                    || playerHealth < pending.PlayerHealth || actor.GetHealth() < pending.ActorHealth)
                {
                    Cancel(entry, reason.Length == 0 ? "Quiet time stopped because health or the player changed." : reason);
                    continue;
                }
                pending.Seconds += Math.Max(0, now - pending.LastUpdate);
                pending.LastUpdate = now;
                pending.PlayerHealth = playerHealth;
                pending.ActorHealth = actor.GetHealth();
                if (pending.Seconds < 30) continue;
                entry.Bond = Math.Min(100, entry.Bond + 5);
                entry.Pending = null;
                entry.Feedback = "Quiet time completed.";
                ReserveCooldown(entry);
                StoreWorld();
            }
        }
        catch (Exception)
        {
            CancelAll("Romance interactions are temporarily unavailable.");
            _actorAvailable = false;
            _actor = null;
            _status = "Romance interactions are temporarily unavailable.";
        }
        finally { PublishView(); }
    }

    // Menu callbacks consume only managed state. Native reads belong to Update/TryAct.
    internal RomanceView View(ulong account)
    {
        var view = _view;
        return view.Accounts.TryGetValue(account, out var value) ? value : view.Default;
    }

    private RomanceView CreateView(Progress? progress, RomanceView? previous = null)
    {
        var bond = progress?.Bond ?? 0;
        var available = IsReady && _hostEnabled && _actorAvailable;
        var seconds = Math.Clamp((int)(progress?.Pending?.Seconds ?? 0), 0, 30);
        var stage = bond >= 75 ? "Close (mod)" : bond >= 40 ? "Trusted (mod)"
            : bond >= 15 ? "Familiar (mod)" : "Acquaintance (mod)";
        var feedback = _saveProblem.Length > 0 ? _saveProblem
            : !IsReady || !_hostEnabled || !_actorAvailable ? _status : progress?.Feedback ?? _status;
        if (previous != null && previous.Available == available && previous.HostEnabled == _hostEnabled
            && previous.Bond == bond && previous.QuietSeconds == seconds && previous.Stage == stage
            && previous.Feedback == feedback) return previous;
        return new(available, _hostEnabled, bond, seconds, stage, feedback);
    }

    private void PublishView()
    {
        var previous = _view;
        var fallback = CreateView(null, previous.Default);
        var changed = !ReferenceEquals(fallback, previous.Default) || previous.Accounts.Count != _accounts.Count;
        if (!changed)
        {
            foreach (var pair in _accounts)
            {
                if (previous.Accounts.TryGetValue(pair.Key, out var old)
                    && ReferenceEquals(CreateView(pair.Value, old), old)) continue;
                changed = true;
                break;
            }
        }
        if (!changed) return;
        var views = new Dictionary<ulong, RomanceView>(_accounts.Count);
        foreach (var pair in _accounts)
        {
            previous.Accounts.TryGetValue(pair.Key, out var old);
            views.Add(pair.Key, CreateView(pair.Value, old));
        }
        _view = new(fallback, views);
    }

    internal bool TryAct(ulong account, PlayerHandle? binding, bool local,
        RomanceAction action, out string feedback)
    {
        feedback = "Romance interactions are unavailable.";
        if (_disposed || !OnThread) return false;
        Refresh(true);
        try
        {
            var settings = _settings();
            if (!IsReady || !settings.HostRomanceEnabled) { feedback = _status; return false; }
            if (!TryIdentity(account, binding, local, out feedback)) return false;
            if (action == RomanceAction.StepBack)
            {
                if (_accounts.TryGetValue(account, out var previous))
                {
                    previous.Bond = 0;
                    previous.Pending = null;
                    // Keep the account's cooldown tombstone across resets and reconnects.
                    previous.Feedback = "Your mod progression was reset. Native relationships are unchanged.";
                    StoreWorld();
                }
                feedback = "Your mod progression was reset. Native relationships are unchanged.";
                return true;
            }
            if (local && !settings.RomanceConsent)
            { Revoke(account); feedback = "Local romance consent is off."; return false; }
            if (!(action switch
                {
                    RomanceAction.Greeting => settings.Greetings,
                    RomanceAction.Appreciation => settings.Appreciation,
                    RomanceAction.QuietCompany => settings.QuietCompany,
                    _ => false
                }))
            { feedback = "That interaction is disabled."; return false; }
            if (!_actorAvailable || _actor == null)
            { feedback = _status; return false; }
            if (!TrySafeCompany(account, binding, local, _actor, action == RomanceAction.QuietCompany,
                    out var player, out var playerHealth, out feedback)) return false;
            if (!_accounts.TryGetValue(account, out var entry))
            {
                if (_accounts.Count >= MaximumAccounts)
                { feedback = "This world's mod progression account limit has been reached."; return false; }
                _accounts.Add(account, entry = new Progress());
            }
            if (entry.Pending != null || Now < entry.NotBeforeMonotonic)
            { feedback = "Please wait at least 60 seconds between interactions."; return false; }
            ReserveCooldown(entry);
            _hasRecord = true;
            if (action == RomanceAction.QuietCompany)
            {
                entry.Pending = new PendingCompany
                {
                    Local = local, Binding = binding, Actor = _actor, Player = player,
                    Stats = local ? LocalPlayer.Stats : null, ActorHealth = _actor.GetHealth(),
                    PlayerHealth = playerHealth, LastUpdate = Now
                };
                feedback = "Quiet time started. No mod bond is added until 30 safe seconds are complete.";
            }
            else
            {
                entry.Bond = Math.Min(100, entry.Bond + (action == RomanceAction.Greeting ? 1 : 2));
                feedback = action == RomanceAction.Greeting
                    ? "Greeting recorded."
                    : "Appreciation recorded.";
            }
            entry.Feedback = feedback;
            StoreWorld();
            return true;
        }
        catch (Exception)
        {
            Revoke(account);
            feedback = "The interaction could not be safely completed.";
            return false;
        }
        finally { PublishView(); }
    }

    internal void Revoke(ulong account)
    {
        if (_disposed || !OnThread) return;
        if (_accounts.TryGetValue(account, out var entry))
            Cancel(entry, "Quiet time stopped because the player session or consent changed.");
        PublishView();
    }

    private static bool TryIdentity(ulong account, PlayerHandle? binding, bool local, out string reason)
    {
        reason = "The current player identity could not be verified.";
        if (account == 0) return false;
        if (local)
        {
            if (BoltNetwork.isRunning && !BoltNetwork.isServer || SteamUser.GetSteamID().m_SteamID != account)
                return false;
            var transform = LocalPlayer.Transform;
            var stats = LocalPlayer.Stats;
            if (transform == null || transform.WasCollected || !transform
                || stats == null || stats.WasCollected || !stats) return false;
        }
        else if (!BoltNetwork.isRunning || !BoltNetwork.isServer || binding == null || binding.IsLocalPlayer
            || binding.PlayerSession == null || !binding.PlayerSession.IsCurrent
            || binding.PlayerSession.RegisteredSteamId != account
            || !ServerWorld.TryValidatePlayerBinding(binding, out _)) return false;
        reason = "";
        return true;
    }

    private static bool TrySafeCompany(ulong account, PlayerHandle? binding, bool local,
        VailActor actor, bool requireHealth, out Transform? player, out float playerHealth, out string reason)
    {
        player = null;
        playerHealth = 0;
        if (!TryIdentity(account, binding, local, out reason)) return false;
        if (!TryActorAuthority(actor, out reason)) return false;
        reason = "Virginia needs to be healthy, calm and free of immediate danger.";
        if (actor.WasCollected || !actor || !actor.isActiveAndEnabled || !actor.IsInitialized()
            || actor.IsDead() || actor.IsDying() || actor.IsOnFire() || actor.IsSleeping()
            || actor.IsScared() || actor.IsAngry() || actor.IsAlerted() || actor.IsInCombatAwareState()
            || actor._meleeIsActive || actor.GetState() != State.World) return false;
        var actorHealth = actor.GetHealth();
        if (!float.IsFinite(actorHealth) || actorHealth <= 0) return false;
        Vector3 position;
        if (local)
        {
            var stats = LocalPlayer.Stats;
            reason = "The local player must be alive, healthy and not busy.";
            if (stats is null || stats.WasCollected || !stats) return false;
            var vitals = stats._vitals;
            var anim = LocalPlayer.AnimControl;
            if (stats.IsDead() || stats.PlayerIsDowned
                || vitals is null || vitals.WasCollected || !vitals || !vitals.IsAlive() || vitals.IsLowHealth
                || anim is null || anim.WasCollected || !anim || anim.injured || anim.doingFullBodyAction
                || anim.IsInDeathState() || anim.IsMidHitReact() || anim.IsMidKnockdown()) return false;
            if (requireHealth)
            {
                reason = "Quiet time needs a current local health observation.";
                if (!LocalPlayerReads.TryReadVitals(out var observed, out _)
                    || observed.LifeState != PlayerLifeState.Alive || observed.HasDied
                    || observed.IsDowned || observed.IsRespawning || observed.IsSeated || observed.IsOnFire)
                    return false;
                playerHealth = observed.Health.Current;
                if (!float.IsFinite(playerHealth) || playerHealth <= 0
                    || playerHealth < observed.Health.Minimum || playerHealth > observed.Health.Maximum)
                    return false;
            }
            player = LocalPlayer.Transform;
            if (player is null || player.WasCollected || !player) return false;
            position = player.position;
        }
        else
        {
            reason = "The remote player must have a current, alive and idle player attachment.";
            if (!ServerWorld.TryReadPlayerLife(binding!, out var life, out _)
                || life.LifeState != PlayerLifeState.Alive
                || !ServerWorld.TryReadPlayerMovement(binding!, out var movement, out _)
                || movement.IsInMidAction || movement.InMenus || movement.ZipLineAttached || movement.InWater)
                return false;
            if (requireHealth)
            {
                reason = "Remote quiet time is unavailable: the installed SDK cannot verify remote health.";
                return false;
            }
            position = new Vector3(life.Entity.Position.X, life.Entity.Position.Y, life.Entity.Position.Z);
        }
        reason = "Stay within four metres of Virginia and away from combat.";
        var actorTransform = actor.transform;
        if (actorTransform == null || actorTransform.WasCollected || !actorTransform) return false;
        var actorPosition = actorTransform.position;
        if (!Finite(position) || !Finite(actorPosition) || (position - actorPosition).sqrMagnitude > 16
            || VailActorManager.IsPlayerInCombat(position, 30, 8, true, false)) return false;
        reason = "";
        return true;
    }

    private static bool TryActorAuthority(VailActor actor, out string reason)
    {
        reason = "Virginia must have a current host-owned network attachment.";
        if (actor.WasCollected || !actor) return false;
        if (!BoltNetwork.isRunning) { reason = ""; return true; }
        if (!BoltNetwork.isServer) return false;
        var networked = actor._actorNetworked;
        if (networked == null || networked.WasCollected || !networked || !networked.isActiveAndEnabled)
            return false;
        var reciprocal = networked._vailActor;
        if (reciprocal == null || reciprocal.WasCollected || !reciprocal || reciprocal.Pointer != actor.Pointer)
            return false;
        var entity = networked._entity;
        if (entity == null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner)
            return false;
        var attachment = entity._entity;
        if (attachment == null || attachment.WasCollected) return false;
        var parent = networked.GetComponentInParent<BoltEntity>();
        if (parent == null || parent.WasCollected || !parent || parent.Pointer != entity.Pointer)
            return false;
        reason = "";
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static void ReserveCooldown(Progress entry)
    {
        entry.NotBeforeUtc = DateTimeOffset.UtcNow.AddSeconds(CooldownSeconds);
        entry.NotBeforeMonotonic = Now + CooldownSeconds;
    }

    private static void Cancel(Progress entry, string reason)
    {
        if (entry.Pending == null) return;
        entry.Pending = null;
        entry.Feedback = reason;
    }

    private void CancelAll(string reason)
    {
        foreach (var entry in _accounts.Values) Cancel(entry, reason);
    }

    public void Dispose()
    {
        if (_disposed || !OnThread) return;
        StopPersistence();
        ResetWorld();
        _disposed = true;
        _authority = false;
        PublishView();
    }

    private sealed record ViewSnapshot(RomanceView Default, Dictionary<ulong, RomanceView> Accounts);

    private sealed class Progress
    {
        internal int Bond;
        internal DateTimeOffset NotBeforeUtc;
        internal double NotBeforeMonotonic;
        internal PendingCompany? Pending;
        internal string Feedback = "Mod progression is ready.";
    }

    private sealed class PendingCompany
    {
        internal bool Local;
        internal PlayerHandle? Binding;
        internal VailActor Actor = null!;
        internal Transform? Player;
        internal PlayerStats? Stats;
        internal float ActorHealth, PlayerHealth;
        internal double LastUpdate, Seconds;
    }
}
