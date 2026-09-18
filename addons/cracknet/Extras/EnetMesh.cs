using Godot;

namespace CrackNet.Extras;

/// <summary>
/// A full ENet mesh over a local network, for running a session in several windows or over a LAN. Guests send state
/// straight to each other in this model, so a star through the host would add a hop to every sample: the host hands
/// out compact peer ids over a temporary ENet lobby, then every pair owns one <see cref="ENetConnection"/>.
/// <para>
/// A development tool, like <see cref="NetworkSimulator"/> and autoconnect: it needs a reachable port per pair, which
/// rules it out over the internet. There, hand CrackNet a transport that is a mesh already - the Steam peer in
/// <c>examples/steam</c> - or any other <c>MultiplayerPeer</c>; CrackNet never creates one itself.
/// </para>
/// </summary>
public partial class EnetMesh : Node
{
    private const int MaxPeers = 8;
    // The rendezvous is the base port itself. Editor autoconnect elects the host by who binds that port first; a host
    // that moved its lobby elsewhere left the base port free, and the next instance to start became a second host
    private const int RendezvousOffset = 0;
    private const int LinkOffset = 200;

    private readonly Dictionary<int, string> _knownPeers = [];
    private readonly Dictionary<int, int> _assignedByLobbyPeer = [];
    private readonly List<(int Remote, ENetConnection Connection)> _waiting = [];
    private readonly List<ENetConnection> _activeConnections = [];

    private ENetMultiplayerPeer? _lobby;
    private ENetMultiplayerPeer? _mesh;
    private NetworkSimulator.Profile _profile = NetworkSimulator.Profile.Clear;
    private string _hostAddress = "127.0.0.1";
    private int _port;
    private int _nextPeer = 2;
    private bool _joinSent;

    public event Action<int>? MeshReady;

    public Error Host(int port, NetworkSimulator.Profile profile)
    {
        _port = port;
        _profile = profile;
        _knownPeers[1] = "";

        _lobby = new ENetMultiplayerPeer();
        var error = _lobby.CreateServer(RendezvousPort, MaxPeers - 1);
        if (error != Error.Ok) return error;
        return StartMesh(1, new Dictionary<int, string>());
    }

    public Error Join(string address, int port, NetworkSimulator.Profile profile)
    {
        _port = port;
        _profile = profile;
        _hostAddress = address;
        _lobby = new ENetMultiplayerPeer();
        return _lobby.CreateClient(address, RendezvousPort);
    }

    private int RendezvousPort => _port + RendezvousOffset;

    private int LinkPort(int local, int remote) => _port + LinkOffset + local * MaxPeers + remote;

    public override void _Process(double delta)
    {
        _lobby?.Poll();
        if (_lobby is { } lobby)
        {
            if (!lobby.GetUniqueId().Equals(1) && !_joinSent
                && lobby.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
            {
                lobby.SetTargetPeer(1);
                lobby.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;
                lobby.PutPacket([0x4a]);
                _joinSent = true;
            }

            while (lobby.GetAvailablePacketCount() > 0)
            {
                var sender = lobby.GetPacketPeer();
                var packet = lobby.GetPacket();
                if (lobby.GetUniqueId() == 1) Welcome(sender);
                else AcceptWelcome(packet);
            }
        }

        for (var i = 0; i < _waiting.Count;)
        {
            var (remote, connection) = _waiting[i];
            var activity = connection.Service(0);
            if (activity.Count == 0 || activity[0].AsInt32() != (int)ENetConnection.EventType.Connect)
            {
                i++;
                continue;
            }

            if (_mesh!.AddMeshPeer(remote, connection) != Error.Ok)
            {
                i++;
                continue;
            }
            _activeConnections.Add(connection);
            _waiting.RemoveAt(i);
        }
    }

    private void Welcome(int lobbyPeer)
    {
        if (_assignedByLobbyPeer.ContainsKey(lobbyPeer)) return;
        if (_nextPeer > MaxPeers) return;

        var assigned = _nextPeer++;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0x57);
        writer.Write(assigned);
        writer.Write(_knownPeers.Count);
        foreach (var (id, address) in _knownPeers)
        {
            writer.Write(id);
            writer.Write(address);
        }

        _lobby!.SetTargetPeer(lobbyPeer);
        _lobby.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;
        _lobby.PutPacket(stream.ToArray());
        _assignedByLobbyPeer[lobbyPeer] = assigned;
        _knownPeers[assigned] = _lobby.GetPeer(lobbyPeer).GetRemoteAddress();
    }

    private void AcceptWelcome(byte[] packet)
    {
        using var stream = new MemoryStream(packet);
        using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != 0x57) return;

        var assigned = reader.ReadInt32();
        var count = reader.ReadInt32();
        var peers = new Dictionary<int, string>();
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadInt32();
            var address = reader.ReadString();
            peers[id] = id == 1 || address.Length == 0 ? _hostAddress : address;
        }
        _lobby!.Close();
        _lobby = null;
        StartMesh(assigned, peers);
    }

    private Error StartMesh(int local, IReadOnlyDictionary<int, string> existing)
    {
        _mesh = new ENetMultiplayerPeer();
        var error = _mesh.CreateMesh(local);
        if (error != Error.Ok) return error;

        for (var remote = local + 1; remote <= MaxPeers; remote++)
        {
            var listener = new ENetConnection();
            error = listener.CreateHostBound("*", LinkPort(local, remote), 1, 0, 0, 0);
            if (error != Error.Ok) return error;
            _waiting.Add((remote, listener));
        }

        foreach (var (remote, address) in existing)
        {
            var connection = new ENetConnection();
            error = connection.CreateHostBound("*", LinkPort(local, remote), 1, 0, 0, 0);
            if (error != Error.Ok) return error;
            connection.ConnectToHost(address, LinkPort(remote, local), 0, local);
            _waiting.Add((remote, connection));
        }

        MultiplayerPeer peer = _profile == NetworkSimulator.Profile.Clear
            ? _mesh
            : new SimulatedMultiplayerPeer(_mesh, _profile);
        Multiplayer.MultiplayerPeer = peer;
        MeshReady?.Invoke(local);
        return Error.Ok;
    }

    public override void _ExitTree()
    {
        _lobby?.Close();
        // The MultiplayerAPI still belongs to the exiting scene and other siblings query it from their _ExitTree.
        // Let Godot release the mesh and its ENetConnection resources after the tree has finished unwinding.
    }
}
