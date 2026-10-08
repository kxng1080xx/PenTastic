using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

sealed class Mirror : IDisposable
{
    const int MaxInFlight = 3;
    const uint Bitrate = 35_000_000;
    const uint Fps = 120;

    readonly Func<Rectangle> _target;
    readonly int _maxWidth, _maxHeight;
    readonly Action<object> _send;
    readonly Thread _thread;
    volatile bool _stop;
    int _unacked;
    long _lastAck = Environment.TickCount64;
    uint _nextId = 1;
    int _frames;
    readonly System.Threading.Timer _fpsTimer;
    volatile ViewRect _view = ViewRect.Whole;

    public Mirror(Func<Rectangle> target, int maxWidth, int maxHeight, Action<object> send)
    {
        (_target, _maxWidth, _maxHeight, _send) = (target, maxWidth, maxHeight, send);
        _fpsTimer = new System.Threading.Timer(_ => AppStatus.Current.Fps = Interlocked.Exchange(ref _frames, 0), null, 1000, 1000);
        _thread = new Thread(Run) { IsBackground = true, Name = "mirror" };
        _thread.Start();
    }

    public void Ack()
    {
        Volatile.Write(ref _lastAck, Environment.TickCount64);
        if (Interlocked.Decrement(ref _unacked) < 0) Interlocked.Exchange(ref _unacked, 0);
    }

    public void SetView(ViewRect view) => _view = view;

    public static Rectangle Crop(ViewRect v, int width, int height)
    {
        int Px(int f, int size) => (int)Math.Round((double)f * size / 65535);
        int w = Math.Clamp(Px(v.Size, width), 2, width), h = Math.Clamp(Px(v.Size, height), 2, height);
        return new Rectangle(Math.Min(Px(v.X, width), width - w), Math.Min(Px(v.Y, height), height - h), w, h);
    }

    public void Dispose()
    {
        _fpsTimer.Dispose();
        AppStatus.Current.Mirror = null;
        AppStatus.Current.Fps = 0;
        _stop = true;
        _thread.Join(3000);
    }

    public static Size Fit(int width, int height, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1, Math.Min(maxWidth > 0 ? (double)maxWidth / width : 1, maxHeight > 0 ? (double)maxHeight / height : 1));
        if (scale > 0.9) scale = 1;
        return new Size(Math.Max(2, (int)Math.Round(width * scale) & ~1), Math.Max(2, (int)Math.Round(height * scale) & ~1));
    }

    void Run()
    {
        timeBeginPeriod(1);
        try
        {
            while (!_stop)
            {
                try
                {
                    using var pipeline = new Pipeline(this);
                    pipeline.Pump();
                }
                catch (Exception e) when (!_stop)
                {
                    Log.Write($"mirror: {e.Message}; retrying");
                    Thread.Sleep(1000);
                }
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    public static void KeepTimersPrecise()
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        SetProcessInformation(GetCurrentProcess(), 4 , ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    sealed class Pipeline : IDisposable
    {
        static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
        static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
        static readonly object StartupLock = new();
        static bool _mfStarted;

        readonly Mirror _m;
        readonly ID3D11Device _device;
        readonly ID3D11DeviceContext _ctx;
        readonly IDXGIOutputDuplication _dup;
        readonly ID3D11Texture2D _bgra;
        readonly ID3D11VideoDevice _vdev;
        readonly ID3D11VideoContext _vctx;
        readonly ID3D11VideoProcessorEnumerator _vpEnum;
        readonly ID3D11VideoProcessor _vp;
        readonly ID3D11VideoProcessorInputView _inView;
        readonly ID3D11Texture2D[] _nv12;
        readonly ID3D11VideoProcessorOutputView[] _outViews;
        readonly IMFDXGIDeviceManager _manager;
        readonly IMFTransform _mft;
        readonly IMFMediaEventGenerator _events;
        readonly Thread _output;
        readonly SemaphoreSlim _needInput = new(0);
        readonly int _w, _h, _sw, _sh;
        readonly Rectangle _target;
        readonly VideoProcessorFilterRange? _sharpen;
        ViewRect? _cropped;
        bool _newStream = true;
        volatile bool _closing;

        public Pipeline(Mirror m)
        {
            _m = m;
            lock (StartupLock)
                if (!_mfStarted)
                {
                    MediaFactory.MFStartup(true).CheckError();
                    _mfStarted = true;
                }

            var target = _target = m._target();
            var center = new Point(target.X + target.Width / 2, target.Y + target.Height / 2);
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? adapter = null;
            IDXGIOutput1? output = null;
            for (uint i = 0; output == null && factory.EnumAdapters1(i, out var a).Success; i++)
            {
                for (uint j = 0; output == null && a.EnumOutputs(j, out var o).Success; j++)
                {
                    var r = o.Description.DesktopCoordinates;
                    if (center.X >= r.Left && center.X < r.Right && center.Y >= r.Top && center.Y < r.Bottom) output = o.QueryInterface<IDXGIOutput1>();
                    o.Dispose();
                }
                if (output != null) adapter = a;
                else a.Dispose();
            }
            if (adapter == null || output == null) throw new InvalidOperationException("no display output for the target monitor");

            try
            {
                var gpu = adapter.Description1;
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device!).CheckError();
                _ctx = _device.ImmediateContext;
                using (var mt = _device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
                _dup = output.DuplicateOutput(_device);

                int sw = _sw = (int)_dup.Description.ModeDescription.Width, sh = _sh = (int)_dup.Description.ModeDescription.Height;
                var size = Fit(sw, sh, m._maxWidth, m._maxHeight);
                (_w, _h) = (size.Width, size.Height);
                _bgra = _device.CreateTexture2D(Texture(sw, sh, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource | BindFlags.RenderTarget));

                _vdev = _device.QueryInterface<ID3D11VideoDevice>();
                _vctx = _ctx.QueryInterface<ID3D11VideoContext>();
                _vpEnum = _vdev.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive, InputWidth = (uint)sw, InputHeight = (uint)sh,
                    OutputWidth = (uint)_w, OutputHeight = (uint)_h, InputFrameRate = new Rational(Fps, 1), OutputFrameRate = new Rational(Fps, 1),
                    Usage = VideoUsage.PlaybackNormal,
                });
                _vp = _vdev.CreateVideoProcessor(_vpEnum, 0);
                if (_vpEnum.VideoProcessorCaps.FilterCaps.HasFlag(VideoProcessorFilterCaps.EdgeEnhancement))
                    _sharpen = _vpEnum.GetVideoProcessorFilterRange(VideoProcessorFilter.EdgeEnhancement);
                _inView = _vdev.CreateVideoProcessorInputView(_bgra, _vpEnum, new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
                _nv12 = Enumerable.Range(0, 3).Select(_ => _device.CreateTexture2D(Texture(_w, _h, Format.NV12, BindFlags.RenderTarget))).ToArray();
                _outViews = _nv12.Select(t => _vdev.CreateVideoProcessorOutputView(t, _vpEnum,
                    new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D })).ToArray();

                string vendor = $"VEN_{gpu.VendorId:X4}";
                using var encoders = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x4 | 0x40,
                    new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
                    new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
                var activate = encoders.FirstOrDefault(e => Try(() => e.GetString(TransformAttributeKeys.MftEnumHardwareVendorIdAttribute)) is { } v
                                                            && v.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                               ?? throw new InvalidOperationException($"no hardware H.264 encoder for {gpu.Description}");
                string encoderName = Try(() => activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)) ?? "encoder";
                _mft = activate.ActivateObject<IMFTransform>();
                _mft.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                _mft.Attributes.Set(LowLatency, 1u);
                var codec = (ICodecAPI)Marshal.GetObjectForIUnknown(_mft.NativePointer);
                SetCodec(codec, "9c27891a-ed7a-40e1-88e8-b22727a024ee", true);
                SetCodec(codec, "8d390aac-dc5c-4200-b57f-814d04babab2", 0u);
                SetCodec(codec, "1c0608e9-370c-4710-8a58-cb6181c42423", 0u);
                SetCodec(codec, "98332df8-03cd-476b-89fa-3f9e442dec9f", gpu.VendorId == 0x10DE ? 70u : 0u);

                _manager = MediaFactory.MFCreateDXGIDeviceManager();
                _manager.ResetDevice(_device);
                _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_manager.NativePointer);
                using (var outType = VideoType(VideoFormatGuids.H264))
                {
                    outType.Set(MediaTypeAttributeKeys.AvgBitrate, Bitrate);
                    outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1);
                    outType.Set(Profile, 66u);
                    _mft.SetOutputType(0, outType, 0);
                }
                using (var inType = VideoType(VideoFormatGuids.NV12)) _mft.SetInputType(0, inType, 0);

                _events = _mft.QueryInterface<IMFMediaEventGenerator>();
                _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                _output = new Thread(OutputLoop) { IsBackground = true, Name = "mirror-output" };
                _output.Start();
                Log.Write($"mirror: {gpu.Description}, {encoderName}, {sw}x{sh} -> {_w}x{_h}, sharpening when zoomed: {(_sharpen != null ? "yes" : "no")}");
                AppStatus.Current.Mirror = $"{_w} × {_h} · {encoderName.Replace("®", "").Replace(" MFT", "")}";
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                output.Dispose();
                adapter.Dispose();
            }
        }

        IMFMediaType VideoType(Guid subtype)
        {
            var t = MediaFactory.MFCreateMediaType();
            t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            t.Set(MediaTypeAttributeKeys.Subtype, subtype);
            t.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)_w << 32) | (uint)_h);
            t.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)Fps << 32) | 1);
            t.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            return t;
        }

        static Texture2DDescription Texture(int w, int h, Format format, BindFlags bind) => new()
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = bind,
        };

        public void Pump()
        {
            const int WaitTimeout = unchecked((int)0x887A0027);
            bool dirty = false, haveImage = false;
            int slot = 0;
            long time = 0;
            while (!_m._stop && _m._target() == _target)
            {
                var r = _dup.AcquireNextFrame(0, out var info, out var resource);
                if (r.Success)
                {
                    if (info.LastPresentTime != 0 || !haveImage)
                    {
                        using var tex = resource.QueryInterface<ID3D11Texture2D>();
                        _ctx.CopyResource(_bgra, tex);
                        dirty = haveImage = true;
                    }
                    resource.Dispose();
                    _dup.ReleaseFrame();
                }
                else if (r.Code == WaitTimeout) Thread.Sleep(1);
                else r.CheckError();

                if (Environment.TickCount64 - Volatile.Read(ref _m._lastAck) > 1000) Interlocked.Exchange(ref _m._unacked, 0);
                var view = _m._view;
                if (view != _cropped)
                {
                    var c = Crop(view, _sw, _sh);
                    _vctx.VideoProcessorSetStreamSourceRect(_vp, 0, true, new Vortice.RawRect(c.Left, c.Top, c.Right, c.Bottom));
                    _cropped = view;
                    if (_sharpen is { } range)
                        _vctx.VideoProcessorSetStreamFilter(_vp, 0, VideoProcessorFilter.EdgeEnhancement, view != ViewRect.Whole, range.Default + (range.Maximum - range.Default) / 2);
                    dirty = haveImage;
                }
                if (!dirty) continue;
                if (Volatile.Read(ref _m._unacked) >= MaxInFlight || !_needInput.Wait(0)) continue;

                _vctx.VideoProcessorBlt(_vp, _outViews[slot], 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = _inView }]);
                using (var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, _nv12[slot], 0, false))
                using (var sample = MediaFactory.MFCreateSample())
                {
                    sample.AddBuffer(buffer);
                    sample.SampleTime = time;
                    sample.SampleDuration = 10_000_000 / Fps;
                    time += 10_000_000 / Fps;
                    _mft.ProcessInput(0, sample, 0);
                }
                Interlocked.Increment(ref _m._unacked);
                slot = (slot + 1) % _nv12.Length;
                dirty = false;
            }
        }

        void OutputLoop()
        {
            try
            {
                while (true)
                {
                    using var ev = _events.GetEvent(0);
                    if (ev.EventType == MediaEventTypes.TransformNeedInput) _needInput.Release();
                    else if (ev.EventType == MediaEventTypes.TransformHaveOutput) Deliver();
                    else if (ev.EventType == MediaEventTypes.TransformDrainComplete) return;
                }
            }
            catch (Exception e)
            {
                if (!_closing) Log.Write($"mirror output: {e.Message}");
            }
        }

        void Deliver()
        {
            var odb = new OutputDataBuffer { StreamID = 0 };
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
            if (hr.Code == unchecked((int)0xC00D6D61))
            {
                using var type = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, type, 0);
                return;
            }
            hr.CheckError();
            using var sample = odb.Sample;
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var ptr, out _, out var length);
            var data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);
            buffer.Unlock();

            var flags = Try(() => sample.GetUInt32(CleanPoint)) == 1 ? VideoFlags.Key : VideoFlags.None;
            if (_newStream) flags |= VideoFlags.NewStream;
            _newStream = false;
            uint id = _m._nextId++;
            Interlocked.Increment(ref _m._frames);
            foreach (var chunk in Protocol.SplitFrame(id, flags, (ushort)_w, (ushort)_h, data)) _m._send(chunk);
        }

        public void Dispose()
        {
            _closing = true;
            try { _mft?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch { }
            if (_output != null && !_output.Join(1000))
                try { using var shutdown = _mft!.QueryInterface<IMFShutdown>(); shutdown.Shutdown(); _output.Join(500); } catch { }
            _events?.Dispose();
            _mft?.Dispose();
            _manager?.Dispose();
            foreach (var v in _outViews ?? []) v.Dispose();
            foreach (var t in _nv12 ?? []) t.Dispose();
            _inView?.Dispose();
            _vp?.Dispose();
            _vpEnum?.Dispose();
            _vctx?.Dispose();
            _vdev?.Dispose();
            _bgra?.Dispose();
            _dup?.Dispose();
            _ctx?.Dispose();
            _device?.Dispose();
        }

        static void SetCodec(ICodecAPI codec, string api, object value)
        {
            var guid = new Guid(api);
            int hr = codec.SetValue(ref guid, ref value);
            if (hr != 0) Log.Write($"encoder setting {api[..8]} not applied (0x{hr:x8})");
        }

        static T? Try<T>(Func<T> f)
        {
            try { return f(); }
            catch { return default; }
        }
    }

    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api, out object min, out object max, out object step);
        [PreserveSig] int GetParameterValues(ref Guid api, IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue(ref Guid api, out object value);
        [PreserveSig] int GetValue(ref Guid api, out object value);
        [PreserveSig] int SetValue(ref Guid api, ref object value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);
}
