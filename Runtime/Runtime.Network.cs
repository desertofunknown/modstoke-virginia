using System.Text;
using SonsSdk.Networking;
using UdpKit;

namespace Modstoke.Virginia;

internal sealed partial class VirginiaRuntime
{
    private sealed class RelationshipEvent(VirginiaRuntime service) : Packets.NetEvent
    {
        private const int Limit = 768;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public override string Id => "modstoke.virginia.relationship.v1";
        public override bool AllowClientRelay => false;

        public override void Read(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!service.OnThread || service._disposed || service._failed || service._loading || !BoltNetwork.isRunning
                || fromConnection == null || fromConnection.WasCollected || packet == null || packet.WasCollected
                || !service._receiveRate.Take(Now, 64, 32)) return;
            try
            {
                var server = BoltNetwork.isServer;
                var envelope = server ? 7 : 11;
                if (packet.Size < (envelope + 4) * 8 || packet.Size > (envelope + Limit + 2) * 8) return;
                var size = packet.ReadUShort();
                if (size < 2 || size > Limit || packet.Size != (envelope + 2 + size) * 8) return;
                var bytes = new byte[size];
                for (var i = 0; i < size; i++) bytes[i] = packet.ReadByte();
                using var stream = new MemoryStream(bytes, false);
                using var reader = new BinaryReader(stream, Utf8);
                if (reader.ReadByte() != 1) return;
                var kind = reader.ReadByte();
                if (server)
                {
                    if (kind == 1)
                    {
                        var nonce = GuidValue(reader);
                        if (stream.Position == stream.Length) service.ReceiveHello(fromConnection, nonce);
                    }
                    else if (kind == 3)
                    {
                        var epoch = GuidValue(reader); var token = GuidValue(reader);
                        var sequence = reader.ReadInt64(); var action = reader.ReadByte();
                        if (stream.Position == stream.Length && sequence > 0 && action is >= 1 and <= 4)
                            service.ReceiveAction(fromConnection, epoch, token, sequence, (RomanceAction)action);
                    }
                    else if (kind == 4)
                    {
                        var epoch = GuidValue(reader); var token = GuidValue(reader);
                        if (stream.Position == stream.Length) service.ReceiveLeave(fromConnection, epoch, token);
                    }
                }
                else if (kind == 2 && service._preferences.Value.RomanceConsent && service.CurrentServer()
                    && fromConnection.Pointer == service._serverPointer
                    && fromConnection.ConnectionId == service._serverConnection)
                {
                    var nonce = GuidValue(reader); var epoch = GuidValue(reader); var token = GuidValue(reader);
                    var sequence = reader.ReadInt64(); var acknowledged = reader.ReadInt64();
                    var available = Boolean(reader); var enabled = Boolean(reader);
                    var actions = reader.ReadByte();
                    var bond = reader.ReadInt32(); var remaining = reader.ReadInt32();
                    var stage = Text(reader); var feedback = Text(reader);
                    if (stream.Position != stream.Length || nonce != service._nonce || epoch == Guid.Empty
                        || token == Guid.Empty || sequence <= 0 || acknowledged < 0 || bond is < 0 or > 100
                        || remaining is < 0 or > 30 || actions > 7) return;
                    if (service._token != token || service._epoch != epoch)
                    {
                        if (acknowledged != 0) return;
                        service._receivedSequence = service._commandSequence = 0;
                    }
                    if (sequence <= service._receivedSequence || acknowledged > service._commandSequence) return;
                    service._epoch = epoch;
                    service._token = token;
                    service._receivedSequence = sequence;
                    service._lastSnapshot = Now;
                    service._remoteActions = actions;
                    service._remote = new(available, enabled, bond, remaining, stage, feedback);
                    if (acknowledged == service._commandSequence) service.Feedback = feedback;
                }
            }
            catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
            {
                // Reject malformed frames without affecting native packets or other mods.
            }
            catch (Exception error) { service.Fail(error); }
        }

        internal void Hello() => Emit(null, writer => { Head(writer, 1); GuidValue(writer, service._nonce); });
        internal void Request(RomanceAction action, long sequence) => Emit(null, writer =>
        {
            Head(writer, 3); GuidValue(writer, service._epoch); GuidValue(writer, service._token);
            writer.Write(sequence); writer.Write((byte)action);
        });
        internal void Leave() => Emit(null, writer =>
        { Head(writer, 4); GuidValue(writer, service._epoch); GuidValue(writer, service._token); });
        internal void Reply(Peer peer, RomanceView view, string? result = null) => Emit(peer, writer =>
        {
            Head(writer, 2); GuidValue(writer, peer.Nonce); GuidValue(writer, service._epoch); GuidValue(writer, peer.Token);
            writer.Write(++peer.SnapshotSequence); writer.Write(peer.LastAction);
            writer.Write(view.Available); writer.Write(view.HostEnabled); writer.Write(service.RemoteActions);
            writer.Write(view.Bond); writer.Write(view.QuietSeconds);
            Text(writer, view.Stage);
            Text(writer, result ?? view.Feedback);
        });

        private void Emit(Peer? peer, Action<BinaryWriter> write)
        {
            if (service._disposed || !BoltNetwork.isRunning
                || (peer == null ? !service.CurrentServer() : !CurrentPeer(peer))) return;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Utf8, true)) write(writer);
            if (stream.Length > Limit) throw new InvalidDataException("Virginia packet exceeds its bound.");
            var bytes = stream.ToArray();
            var packet = peer == null ? NewPacket(bytes.Length + 2, Bolt.GlobalTargets.OnlyServer)
                : NewPacket(bytes.Length + 2, peer.Connection);
            packet.Packet.WriteUShort((ushort)bytes.Length);
            foreach (var value in bytes) packet.Packet.WriteByte(value);
            Send(packet);
        }

        private static void Head(BinaryWriter writer, byte kind) { writer.Write((byte)1); writer.Write(kind); }
        private static void GuidValue(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
        private static Guid GuidValue(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(16);
            return bytes.Length == 16 ? new Guid(bytes) : throw new EndOfStreamException();
        }
        private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch
        { 0 => false, 1 => true, _ => throw new InvalidDataException() };
        private static void Text(BinaryWriter writer, string value)
        {
            var bytes = Utf8.GetBytes(value);
            if (bytes.Length > 256) throw new InvalidDataException();
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static string Text(BinaryReader reader)
        {
            var size = reader.ReadUInt16();
            if (size > 256) throw new InvalidDataException();
            var bytes = reader.ReadBytes(size);
            if (bytes.Length != size) throw new EndOfStreamException();
            return Utf8.GetString(bytes);
        }
    }
}
