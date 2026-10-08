using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

enum Transport { Usb, WiFi, Bluetooth }

sealed class Server(PenInjector pen, CanvasHost canvas, Pairing pairing)
{
    readonly object _gate = new();
    Action? _current;
    Action<object>? _send;

    public void SendToPhone(object message)
    {
        Action<object>? send;
        lock (_gate) send = _send;
        try { send?.Invoke(message); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ = AnswerDiscoveryAsync(ct);
        _ = BluetoothServer.RunAsync((input, output, remote, close) => Start(input, output, remote, Transport.Bluetooth, close), ct);
        var listener = new TcpListener(IPAddress.Any, Protocol.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            Log.Write($"cannot listen on port {Protocol.Port}: {e.Message}");
            AppStatus.Current.Problem = $"Port {Protocol.Port} is in use. Is PenTastic already running?";
            return;
        }
        Log.Write($"listening on port {Protocol.Port}");
        AppStatus.Current.SetWaiting();

        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                var stream = client.GetStream();
                Start(stream, stream, remote.ToString(), IPAddress.IsLoopback(remote.Address) ? Transport.Usb : Transport.WiFi, client.Close);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    void Start(Stream input, Stream output, string remote, Transport transport, Action close) =>
        new Thread(() => Session(input, output, remote, transport, close)) { IsBackground = true, Name = "session" }.Start();

    async Task AnswerDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
            while (true)
            {
                var r = await udp.ReceiveAsync(ct);
                if (!Protocol.IsDiscoveryRequest(r.Buffer)) continue;
                var t = Mapping.Describe(pen.Target);
                await udp.SendAsync(Protocol.DiscoveryReply(pairing.ServerId, Protocol.Port, Environment.MachineName, t.Width, t.Height,
                    (byte)t.Monitors.Length), r.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException e)
        {
            Log.Write($"discovery off: {e.Message}");
        }
    }

    void Session(Stream input, Stream output, string remote, Transport transport, Action close)
    {
        var who = remote;
        bool usb = transport == Transport.Usb;
        Mirror? mirror = null;
        var keys = new KeyInjector();
        FrameCipher? cipher = null;
        bool welcomed = false;
        long lastReceived = Environment.TickCount64;
        void Send(object m)
        {
            var bytes = Protocol.Encode(m);
            lock (output)
            {
                output.Write(cipher?.Seal(bytes) ?? bytes);
                output.Flush();
            }
        }
        byte[] Receive()
        {
            var body = Protocol.ReadBody(input);
            Volatile.Write(ref lastReceived, Environment.TickCount64);
            return cipher?.Open(body) ?? body;
        }

        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (Environment.TickCount64 - Volatile.Read(ref lastReceived) > 3000) close();
            else if (welcomed)
                try { Send(Heartbeat.Instance); } catch { }
        }, null, 1000, 1000);

        try
        {
            var nonce = RandomNumberGenerator.GetBytes(16);
            Send(new ServerHello(Protocol.Version, pairing.ServerId, nonce, Environment.MachineName));
            if (Protocol.Decode(Receive()) is not ClientHello hello) throw new InvalidDataException("expected CLIENT_HELLO");
            who = $"{hello.Name} ({remote})";

            if (hello.Version != Protocol.Version)
            {
                Send(new Reject(RejectReason.Version, $"The PC uses protocol v{Protocol.Version} and the phone v{hello.Version}. Update both apps."));
                Log.Write($"rejected {who}: version {hello.Version}");
                return;
            }
            var secret = pairing.Authenticate(hello, nonce, usb, out var reason);
            if (secret == null)
            {
                Send(new Reject(reason, reason == RejectReason.BadToken
                    ? "That pairing code has expired. On the PC, open Pair a phone again and scan the new code."
                    : "This PC doesn't know this phone. On the PC, open Pair a phone and scan the code."));
                Log.Write($"rejected {who}: {reason}");
                return;
            }

            Action? old;
            lock (_gate) (old, _current, _send) = (_current, close, null);
            if (old != null)
            {
                old();
                pen.Release();
            }
            Send(new Welcome(new byte[32], Mapping.Describe(pen.Target)));
            if (!usb) lock (output) cipher = new FrameCipher(secret, nonce, hello.ClientId, server: true);
            welcomed = true;
            lock (_gate) if (_current == close) _send = Send;
            Log.Write($"connected: {who} over {transport}");
            AppStatus.Current.SetConnected(hello.Name, transport switch { Transport.Usb => "USB", Transport.WiFi => "Wi-Fi", _ => "Bluetooth" });

            var style = new DrawState(PenMode.Control, BrushKind.Pen, 0xFFFFFFFF, 300);
            var view = ViewRect.Whole;
            while (true)
            {
                switch (Protocol.Decode(Receive()))
                {
                    case Samples m when style.Mode == PenMode.Draw:
                        canvas.Draw(m.Items, style);
                        break;
                    case Samples m:
                        foreach (var s in m.Items)
                            pen.Handle(s.Kind, s.X / 65535.0, s.Y / 65535.0, s.Pressure / 65535.0, s.TiltX, s.TiltY, s.Flags);
                        break;
                    case DrawState d:
                        if (d.Mode != style.Mode)
                        {
                            Log.Write($"mode: {d.Mode}");
                            if (d.Mode == PenMode.Draw)
                            {
                                pen.Release();
                                canvas.Show();
                            }
                        }
                        style = d;
                        break;
                    case MirrorRequest q when transport == Transport.Bluetooth:
                        if (q.On) Log.Write("mirror needs USB or Wi-Fi; ignored over Bluetooth");
                        break;
                    case MirrorRequest q:
                        mirror?.Dispose();
                        mirror = q.On ? new Mirror(() => pen.Target, q.MaxWidth, q.MaxHeight, Send) : null;
                        mirror?.SetView(view);
                        Log.Write(q.On ? $"mirror on (max {q.MaxWidth}x{q.MaxHeight})" : "mirror off");
                        break;
                    case KeyPress k:
                        keys.Handle(k);
                        break;
                    case VideoAck:
                        mirror?.Ack();
                        break;
                    case ViewRect v:
                        view = v;
                        mirror?.SetView(v);
                        break;
                    case PaperChoice p:
                        canvas.SetPaper(p.Paper);
                        break;
                    case InkAction a:
                        canvas.Command(a.Command);
                        break;
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"disconnected: {who}: {e.Message}");
        }
        finally
        {
            close();
            mirror?.Dispose();
            keys.ReleaseAll();
            cipher?.Dispose();
            lock (_gate)
            {
                if (_current == close)
                {
                    _current = null;
                    _send = null;
                    pen.Release();
                    AppStatus.Current.SetWaiting();
                }
            }
        }
    }
}
