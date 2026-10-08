using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

static class BluetoothServer
{
    public static readonly Guid ServiceId = new("5d3f0a7e-8c41-4b6e-9f2a-50e4e5435453");

    public static async Task<string?> AddressAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            return adapter?.BluetoothAddress.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            Log.Write($"bluetooth address: {e.Message}");
            return null;
        }
    }

    public static async Task RunAsync(Action<Stream, Stream, string, Action> accept, CancellationToken ct)
    {
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            var listener = new StreamSocketListener();
            try
            {
                var provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceId));
                listener.ConnectionReceived += (_, e) =>
                {
                    var socket = e.Socket;
                    accept(socket.InputStream.AsStreamForRead(0), socket.OutputStream.AsStreamForWrite(0),
                        $"bluetooth {socket.Information.RemoteHostName.DisplayName}", socket.Dispose);
                };
                await listener.BindServiceNameAsync(provider.ServiceId.AsString(), SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                provider.StartAdvertising(listener, radioDiscoverable: false);
                Log.Write("bluetooth: listening");
                AppStatus.Current.Bluetooth = true;
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                provider.StopAdvertising();
                return;
            }
            catch (Exception e)
            {
                var error = $"0x{e.HResult:X8} {e.Message}".Trim();
                if (error != lastError) Log.Write($"bluetooth off: {error}; retrying every 10 s");
                AppStatus.Current.Bluetooth = false;
                lastError = error;
            }
            finally
            {
                listener.Dispose();
            }
            try { await Task.Delay(10_000, ct); }
            catch (OperationCanceledException) { }
        }
    }
}
