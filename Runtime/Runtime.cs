using Endnight.Extensions;
using Modstoke.Sons;
using SonsSdk.Networking;
using Steamworks;

namespace Modstoke.Virginia;

internal sealed partial class VirginiaRuntime : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly VirginiaPreferences _preferences;
    private readonly VirginiaRomance _romance;
    private readonly VirginiaAppearance _appearance = new();
    private readonly VirginiaSecondaryMotion _motion = new();
    private readonly VirginiaSkin _skin = new();
    private readonly Dictionary<Guid, Peer> _peers = new();
    private readonly Rate _receiveRate = new();
    private RelationshipEvent? _event;
    private Guid _world, _epoch, _nonce, _token;
    private IntPtr _serverPointer;
    private uint _serverConnection;
    private long _nextHello, _lastSnapshot, _receivedSequence, _commandSequence, _nextCleanup, _nextAppearance, _nextNetwork;
    private int _mode = -1;
    private byte _remoteActions;
    private ulong _localAccount;
    private bool _consented, _disposed, _failed, _loading;
    private RomanceView _remote = Unavailable("Waiting for the host.");
    internal bool OnThread => Environment.CurrentManagedThreadId == _thread;
    internal bool CanConfigureHost => OnThread && !_disposed && !_failed
        && (!BoltNetwork.isRunning || BoltNetwork.isServer);
    internal string AppearanceStatus => _appearance.Status + " " + _motion.Status + " " + _skin.Status;
    internal string Feedback { get; private set; } = "";
    private static long Now => Environment.TickCount64;
    internal RomanceView View => _failed ? Unavailable("Virginia extension stopped after an error.")
        : _mode == 2 ? _remote : _romance.View(_localAccount);
    private byte HostActions => (byte)((_preferences.Value.Greetings ? 1 : 0)
        | (_preferences.Value.Appreciation ? 2 : 0) | (_preferences.Value.QuietCompany ? 4 : 0));
    private byte RemoteActions => _romance.RemoteQuietCompanySupported ? HostActions : (byte)(HostActions & 3);
    internal bool CanRequest(RomanceAction action)
    {
        var view = View;
        return !_disposed && !_failed && !_loading && _preferences.Value.RomanceConsent
            && view.Available && view.HostEnabled
            && (action == RomanceAction.StepBack
                || (((_mode == 2 ? _remoteActions : HostActions) >> ((int)action - 1)) & 1) != 0);
    }

    internal VirginiaRuntime(VirginiaPreferences preferences)
    {
        _preferences = preferences;
        _romance = new VirginiaRomance(() => preferences.Value);
        try
        {
            SaveLifecycle.Lifecycle += OnSaveLifecycle;
            if (Packets.SupportsClientRelayPolicy)
            {
                _event = new RelationshipEvent(this);
                Packets.Register(_event);
            }
        }
        catch
        {
            SaveLifecycle.Lifecycle -= OnSaveLifecycle;
            try
            {
                if (_event != null) Packets.Unregister(_event);
            }
            finally
            {
                try { _romance.Dispose(); }
                finally
                {
                    try { _appearance.Dispose(); }
                    finally
                    {
                        try { _motion.Dispose(); }
                        finally { _skin.Dispose(); }
                    }
                }
            }
            throw;
        }
    }

    internal void Update()
    {
        if (!OnThread || _disposed || _failed) return;
        try
        {
            _romance.Update();
            if (_loading) return;
            if (Now >= _nextNetwork)
            {
                _nextNetwork = Now + 250;
                UpdateNetwork();
                if (_mode != 2) Feedback = "";
            }
            if (Now >= _nextAppearance)
            {
                _nextAppearance = Now + 500;
                UpdatePresentation(_preferences.Value);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void UpdatePresentation(VirginiaSettings settings)
    {
        var observation = new VirginiaPresentationObservation();
        _appearance.Update(settings, observation);
        _motion.Update(settings, observation);
        _skin.Update(settings.AppearanceEnabled && settings.EnhancedSkin, observation);
    }

    internal void SettingsChanged()
    {
        if (!OnThread || _disposed || _failed) return;
        _nextAppearance = 0;
        _nextNetwork = 0;
        Feedback = "";
        Update();
    }

    internal void LateUpdate()
    {
        if (!OnThread || _disposed || _failed || _loading) return;
        _motion.LateUpdate();
    }

    private void UpdateNetwork()
    {
        if (_loading) return;
        var mode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        var world = mode == 2 ? Guid.Empty : _romance.Epoch;
        if (_mode != mode || _world != world)
        {
            ResetTransport();
            _mode = mode;
            _world = world;
            _epoch = Guid.NewGuid();
        }
        _localAccount = LocalAccount();
        var consent = _preferences.Value.RomanceConsent;
        if (_consented && !consent)
        {
            if (mode == 2 && CurrentServer() && _token != Guid.Empty) _event?.Leave();
            else if (_localAccount != 0) _romance.Revoke(_localAccount);
            ResetClient();
        }
        _consented = consent;
        if (mode == 2)
        {
            var server = BoltNetwork.server;
            if (server == null || server.WasCollected)
            {
                ResetClient();
                _serverPointer = IntPtr.Zero;
                _serverConnection = 0;
                return;
            }
            if (server.Pointer != _serverPointer || server.ConnectionId != _serverConnection)
            {
                ResetClient();
                _serverPointer = server.Pointer;
                _serverConnection = server.ConnectionId;
            }
            if (!consent) { _remote = Unavailable("Participation is off."); return; }
            if (Now - _lastSnapshot > 12000 && _token != Guid.Empty)
            {
                ResetClient();
                Feedback = "Host response expired; any previous action remains unconfirmed.";
            }
            if (Now >= _nextHello)
            {
                _nextHello = Now + 3000;
                _event?.Hello();
                if (_event == null) _remote = Unavailable("A compatible packet transport is required.");
            }
        }
        else if (Now >= _nextCleanup)
        {
            _nextCleanup = Now + 1000;
            foreach (var pair in _peers.ToArray())
            {
                if (!CurrentPeer(pair.Value) || Now - pair.Value.Seen > 12000)
                {
                    _romance.Revoke(pair.Value.Account);
                    _peers.Remove(pair.Key);
                }
            }
        }
    }

    internal bool TryAct(RomanceAction action, out string status)
    {
        status = "Virginia interactions are unavailable.";
        if (!OnThread || _disposed || _failed || _loading || !_preferences.Value.RomanceConsent) return false;
        UpdateNetwork();
        if (!CanRequest(action))
        { status = "This interaction is disabled or unavailable in the current world."; return false; }
        if (_mode == 2)
        {
            if (_event == null || !CurrentServer() || _token == Guid.Empty || !_remote.Available || !_remote.HostEnabled)
            { status = "The host has not enabled Virginia interactions."; return false; }
            if (_commandSequence == long.MaxValue)
            { status = "Reconnect before requesting another interaction."; return false; }
            _event.Request(action, ++_commandSequence);
            status = Feedback = "Request sent; waiting for host confirmation.";
            return true;
        }
        if (_localAccount == 0)
        { status = "Your local account identity is unavailable."; return false; }
        var result = _romance.TryAct(_localAccount, null, true, action, out status);
        Feedback = status;
        return result;
    }

    private void ReceiveHello(BoltConnection connection, Guid nonce)
    {
        if (!_romance.IsReady || !_romance.IsAuthority || _world == Guid.Empty || _world != _romance.Epoch
            || nonce == Guid.Empty || !LiveClient(connection)) return;
        ServerSession? session = null;
        foreach (var candidate in ServerSessions.Sessions)
        {
            if (!candidate.IsCurrent || candidate.ConnectionId != connection.ConnectionId
                || !candidate.RegisteredSteamId.HasValue || candidate.RegisteredSteamId.Value == 0) continue;
            if (session != null) return;
            session = candidate;
        }
        if (session == null) return;
        var account = session.RegisteredSteamId!.Value;
        if (_peers.TryGetValue(session.SessionId, out var peer))
        {
            if (!CurrentPeer(peer) || peer.Pointer != connection.Pointer || !peer.Rate.Take(Now, 4, 4)) return;
            if (peer.Nonce != nonce)
            {
                _romance.Revoke(peer.Account);
                peer.Nonce = nonce;
                peer.Token = Guid.NewGuid();
                peer.LastAction = peer.SnapshotSequence = 0;
            }
        }
        else
        {
            if (_peers.Count >= 128 || _peers.Values.Any(p => p.Account == account && CurrentPeer(p))) return;
            peer = new Peer(session, account, connection.Pointer, connection.ConnectionId, nonce);
            _peers.Add(session.SessionId, peer);
        }
        peer.Seen = Now;
        _event?.Reply(peer, _romance.View(peer.Account));
    }

    private void ReceiveAction(BoltConnection connection, Guid epoch, Guid token, long sequence, RomanceAction action)
    {
        if (epoch != _epoch || !_romance.IsReady || !_romance.IsAuthority || _world != _romance.Epoch) return;
        var peer = _peers.Values.FirstOrDefault(p => p.Token == token && p.Pointer == connection.Pointer
            && p.Connection == connection.ConnectionId);
        if (peer == null || !CurrentPeer(peer) || !peer.Rate.Take(Now, 4, 4)) return;
        peer.Seen = Now;
        if (sequence > peer.LastAction)
        {
            peer.LastAction = sequence;
            if (ServerWorld.TryCapturePlayerBinding(peer.Session, out var binding, out _))
            {
                _romance.TryAct(peer.Account, binding, false, action, out peer.Feedback);
            }
            else peer.Feedback = "The host could not validate your current player.";
        }
        _event?.Reply(peer, _romance.View(peer.Account), peer.Feedback);
    }

    private void ReceiveLeave(BoltConnection connection, Guid epoch, Guid token)
    {
        if (epoch != _epoch) return;
        var peer = _peers.Values.FirstOrDefault(p => p.Token == token && p.Pointer == connection.Pointer
            && p.Connection == connection.ConnectionId && CurrentPeer(p));
        if (peer == null) return;
        _romance.Revoke(peer.Account);
        _peers.Remove(peer.Session.SessionId);
    }

    private bool CurrentServer()
    {
        if (_loading || !BoltNetwork.isRunning || BoltNetwork.isServer) return false;
        var server = BoltNetwork.server;
        return server != null && !server.WasCollected && server.Pointer == _serverPointer
            && server.ConnectionId == _serverConnection;
    }

    private static bool LiveClient(BoltConnection connection)
    {
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer || connection == null || connection.WasCollected) return false;
        var found = false;
        BoltNetwork.clients.ForEach((Action<BoltConnection>)(client =>
        {
            if (client != null && !client.WasCollected && client.Pointer == connection.Pointer
                && client.ConnectionId == connection.ConnectionId) found = true;
        }));
        return found;
    }

    private static bool CurrentPeer(Peer peer)
    {
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer || !peer.Session.IsCurrent
            || peer.Session.RegisteredSteamId != peer.Account) return false;
        var found = false;
        BoltNetwork.clients.ForEach((Action<BoltConnection>)(client =>
        {
            if (client != null && !client.WasCollected && client.Pointer == peer.Pointer
                && client.ConnectionId == peer.Connection) found = true;
        }));
        return found;
    }

    private static ulong LocalAccount()
    {
        try { return SteamUser.GetSteamID().m_SteamID; }
        catch { return 0; }
    }

    private void ResetClient()
    {
        _nonce = Guid.NewGuid();
        _token = Guid.Empty;
        _remoteActions = 0;
        _nextHello = _lastSnapshot = _receivedSequence = _commandSequence = 0;
        _remote = Unavailable("Waiting for the host.");
    }

    private void ResetTransport()
    {
        foreach (var peer in _peers.Values) _romance.Revoke(peer.Account);
        _peers.Clear();
        ResetClient();
        _serverPointer = IntPtr.Zero;
        _serverConnection = 0;
        Feedback = "";
    }

    private void OnSaveLifecycle(SaveLifecycleEvent value)
    {
        if (!OnThread || _disposed || value.Operation != "Load") return;
        if (value.Phase == "Before")
        {
            _loading = true;
            ResetTransport();
            _epoch = Guid.NewGuid();
            _world = Guid.Empty;
            _nextAppearance = 0;
            UpdatePresentation(new VirginiaSettings());
        }
        else if (value.Phase == "After") _loading = value.Success != true;
    }

    private void Fail(Exception error)
    {
        if (_failed) return;
        _failed = true;
        Feedback = "Virginia extension stopped after an error. Other mods remain active.";
        Console.WriteLine("Modstoke Virginia: " + error);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveLifecycle.Lifecycle -= OnSaveLifecycle;
        try
        {
            if (_event != null) Packets.Unregister(_event);
        }
        finally
        {
            _event = null;
            try { _appearance.Dispose(); }
            finally
            {
                try { _motion.Dispose(); }
                finally
                {
                    try { _skin.Dispose(); }
                    finally { _romance.Dispose(); }
                }
            }
        }
    }

    private static RomanceView Unavailable(string text) => new(false, false, 0, 0, "Unavailable", text);

    private sealed class Peer(ServerSession session, ulong account, IntPtr pointer, uint connection, Guid nonce)
    {
        internal readonly ServerSession Session = session;
        internal readonly ulong Account = account;
        internal readonly IntPtr Pointer = pointer;
        internal readonly uint Connection = connection;
        internal readonly Rate Rate = new();
        internal Guid Nonce = nonce, Token = Guid.NewGuid();
        internal long Seen = Now, LastAction, SnapshotSequence;
        internal string Feedback = "";
    }

    private sealed class Rate
    {
        private long _at;
        private double _tokens;
        internal bool Take(long now, int perSecond, int burst)
        {
            if (_at == 0) _tokens = burst;
            _tokens = Math.Min(burst, _tokens + Math.Max(0, now - _at) * perSecond / 1000.0);
            _at = now;
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }
}
