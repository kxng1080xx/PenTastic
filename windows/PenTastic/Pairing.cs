using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

sealed class Pairing
{
    sealed record Client(string Id, string Secret, string Name);
    sealed record Data(string ServerId, List<Client> Clients);

    readonly string _path;
    readonly object _gate = new();
    readonly Data _data;
    byte[]? _token;

    public byte[] ServerId { get; }

    public event Action<string>? Paired;

    public Pairing(string path)
    {
        _path = path;
        Data? data = null;
        try { data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException) { }
        _data = data ?? new Data(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), []);
        ServerId = Convert.FromHexString(_data.ServerId);
        if (data == null) Save();
    }

    public byte[] StartPairing()
    {
        lock (_gate) return _token = RandomNumberGenerator.GetBytes(16);
    }

    public void StopPairing()
    {
        lock (_gate) _token = null;
    }

    public byte[]? Authenticate(ClientHello hello, byte[] nonce, bool loopback, out RejectReason reason)
    {
        reason = RejectReason.NotPaired;
        var id = Convert.ToHexString(hello.ClientId);
        lock (_gate)
        {
            switch (hello.Auth)
            {
                case AuthKind.Loopback when loopback:
                    return [];
                case AuthKind.Token:
                    if (_token == null || !CryptographicOperations.FixedTimeEquals(hello.Proof, Proof(_token, nonce, hello.ClientId)))
                    {
                        reason = RejectReason.BadToken;
                        return null;
                    }
                    var secret = Secret(_token, hello.ClientId);
                    _token = null;
                    _data.Clients.RemoveAll(c => c.Id == id);
                    _data.Clients.Add(new Client(id, Convert.ToBase64String(secret), hello.Name));
                    Save();
                    Log.Write($"paired: {hello.Name}");
                    Paired?.Invoke(hello.Name);
                    return secret;
                case AuthKind.Proof:
                    var client = _data.Clients.FirstOrDefault(c => c.Id == id);
                    if (client == null) return null;
                    var expected = Proof(Convert.FromBase64String(client.Secret), nonce, hello.ClientId);
                    return CryptographicOperations.FixedTimeEquals(hello.Proof, expected) ? Convert.FromBase64String(client.Secret) : null;
                default:
                    return null;
            }
        }
    }

    public static byte[] Proof(byte[] key, byte[] nonce, byte[] clientId) => HMACSHA256.HashData(key, nonce.Concat(clientId).ToArray());

    public static byte[] Secret(byte[] token, byte[] clientId) => HMACSHA256.HashData(token, "PNTC secret"u8.ToArray().Concat(clientId).ToArray());

    public static string Link(byte[] serverId, byte[] token, int port, IEnumerable<string> hosts, string name, string? bluetooth = null) =>
        $"pentastic://pair?id={Convert.ToHexString(serverId)}&t={Convert.ToHexString(token)}&p={port}"
        + $"&h={string.Join(",", hosts)}&n={Uri.EscapeDataString(name)}" + (bluetooth == null ? "" : $"&bt={bluetooth}");

    public static List<string> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => n.GetIPProperties())
        .OrderByDescending(p => p.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
        .SelectMany(p => p.UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(a => a.ToString())
        .Distinct()
        .ToList();

    void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_data));
    }
}
