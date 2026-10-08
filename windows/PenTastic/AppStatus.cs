using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PenTastic;

sealed class AppStatus : INotifyPropertyChanged
{
    public static readonly AppStatus Current = new();

    string? _phone, _transport, _mirror, _problem;
    string _usb = "Starting";
    bool _bluetooth;
    int _fps;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Phone
    {
        get => _phone;
        private set { if (Set(ref _phone, value)) Notify(nameof(Connected), nameof(Headline), nameof(Detail)); }
    }

    public string? Transport
    {
        get => _transport;
        private set { if (Set(ref _transport, value)) Notify(nameof(Detail)); }
    }

    public string Usb { get => _usb; set => Set(ref _usb, value); }
    public bool Bluetooth { get => _bluetooth; set => Set(ref _bluetooth, value); }

    public string? Problem
    {
        get => _problem;
        set { if (Set(ref _problem, value)) Notify(nameof(Detail)); }
    }

    public string? Mirror
    {
        get => _mirror;
        set { if (Set(ref _mirror, value)) Notify(nameof(Mirroring), nameof(MirrorLine)); }
    }

    public int Fps
    {
        get => _fps;
        set { if (Set(ref _fps, value)) Notify(nameof(MirrorLine)); }
    }

    public bool Connected => Phone != null;
    public bool Mirroring => Mirror != null;
    public string Headline => Connected ? "Connected" : "Waiting for your phone";
    public string Detail => Connected ? $"{Phone} · {Transport}"
        : Problem ?? "Open PenTastic on the phone. Plug in USB, or pair once for Wi-Fi and Bluetooth.";
    public string MirrorLine => Mirroring ? $"{Mirror} · {Fps} fps" : "Off. Turn on PC screen in the phone's View menu.";

    public void SetConnected(string phone, string transport)
    {
        Transport = transport;
        Phone = phone;
        Problem = null;
    }

    public void SetWaiting()
    {
        Phone = null;
        Transport = null;
        Mirror = null;
        Fps = 0;
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name!);
        return true;
    }

    void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
