using PvDotNet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;

namespace Lanzhou_v1._0.Camera
{
    /// <summary>
    /// eBUS 相机服务（PvDotNet）：
    /// - 扫描设备
    /// - 连接/断开
    /// - 后台取流 -> 提供最新帧（用于预览/拍照）
    ///
    /// 说明：相机通过 USB 或网口与工控机连接，在软件层面均通过 eBUS SDK 统一访问。
    /// </summary>
    public sealed class EbusCameraService : IDisposable
    {
        private readonly object _lock = new();

        private PvDevice? _device;
        private PvStream? _stream;
        private PvPipeline? _pipeline;
        private Thread? _grabThread;
        private volatile bool _running;

        private readonly PvBufferConverter _converter = new();
        private readonly PvBuffer _converted = new();

        private CameraFrame? _latest;
        private long _frameSeq = 0;
        private long _lastUiPushTick = 0;

        /// <summary>
        /// 帧事件（用于 UI 预览）。回调线程：取流线程。
        /// </summary>
        public event Action<CameraFrame>? FrameArrived;

        public bool IsConnected
        {
            get
            {
                lock (_lock) return _device != null && _device.IsConnected;
            }
        }

        public string StatusText { get; private set; } = "相机未连接";

        public static List<CameraDeviceDescriptor> ScanDevices()
{
    var list = new List<CameraDeviceDescriptor>();
    using var sys = new PvSystem();

    // 不同 eBUS 版本：Find/DeviceCount/GetDeviceCount/GetDeviceInfo 的 API 可能略有差异。
    CallVoid(sys, "Find");

    uint n = GetDeviceCountCompat(sys);
    for (uint i = 0; i < n; i++)
    {
        PvDeviceInfo? info = GetDeviceInfoCompat(sys, i);
        if (info != null)
            list.Add(new CameraDeviceDescriptor(info));
    }
    return list;
}

private static void CallVoid(object obj, string methodName)
{
    var m = obj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
    if (m == null) throw new MissingMethodException(obj.GetType().Name, methodName);
    m.Invoke(obj, null);
}

private static uint GetDeviceCountCompat(PvSystem sys)
{
    var t = sys.GetType();

    // 属性：DeviceCount
    var p = t.GetProperty("DeviceCount", BindingFlags.Public | BindingFlags.Instance);
    if (p != null) return Convert.ToUInt32(p.GetValue(sys, null));

    // 方法：GetDeviceCount()
    var m = t.GetMethod("GetDeviceCount", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
    if (m != null) return Convert.ToUInt32(m.Invoke(sys, null));

    // 兜底：Count
    var p2 = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
    if (p2 != null) return Convert.ToUInt32(p2.GetValue(sys, null));

    throw new MissingMethodException("PvSystem", "DeviceCount/GetDeviceCount");
}

private static PvDeviceInfo? GetDeviceInfoCompat(PvSystem sys, uint index)
{
    var t = sys.GetType();

    // 优先匹配：GetDeviceInfo(uint) / GetDeviceInfo(int)
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
    {
        if (m.Name != "GetDeviceInfo") continue;
        var ps = m.GetParameters();
        if (ps.Length != 1) continue;

        object arg = ps[0].ParameterType == typeof(int) ? (object)(int)index : (object)index;
        var obj = m.Invoke(sys, new[] { arg });
        return obj as PvDeviceInfo;
    }

    // 兜底：GetDeviceInfoByIndex
    var m2 = t.GetMethod("GetDeviceInfoByIndex", BindingFlags.Public | BindingFlags.Instance);
    if (m2 != null)
    {
        var ps = m2.GetParameters();
        object arg = (ps.Length == 1 && ps[0].ParameterType == typeof(int)) ? (object)(int)index : (object)index;
        var obj = m2.Invoke(sys, new[] { arg });
        return obj as PvDeviceInfo;
    }

    throw new MissingMethodException("PvSystem", "GetDeviceInfo");
}


        public void Connect(CameraDeviceDescriptor desc)
        {
            if (desc == null) throw new ArgumentNullException(nameof(desc));

            Disconnect();

            try
            {
                StatusText = "正在连接相机...";

                var info = desc.DeviceInfo;

                _device = PvDevice.CreateAndConnect(info);
                _stream = PvStream.CreateAndOpen(desc.ConnectionId);
                _pipeline = new PvPipeline(_stream);

                // 优化包尺寸（对 GEV 有意义）
                if (_device is PvDeviceGEV gev)
                    gev.NegotiatePacketSize();

                // 尝试自动设置像素格式到彩色（RGB/BGR/Bayer）
                try
                {
                    AutoSelectColorPixelFormat(_device);
                    try { _device.Parameters.SetEnumValue("TriggerMode", "Off"); } catch { }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Auto pixel format failed: " + ex.Message);
                }

                _pipeline.BufferSize = _device.PayloadSize;
                _pipeline.BufferCount = 16;
                _pipeline.Start();

                _device.StreamEnable();
                _device.Parameters.ExecuteCommand("AcquisitionStart");

                _running = true;
                _grabThread = new Thread(GrabLoop) { IsBackground = true, Name = "EbusCameraGrab" };
                _grabThread.Start();

                StatusText = $"已连接：{desc}";
            }
            catch
            {
                Disconnect();
                throw;
            }
        }

        public void Disconnect()
        {
            try { _running = false; } catch { }

            try
            {
                if (_grabThread != null && _grabThread.IsAlive)
                    _grabThread.Join(800);
            }
            catch { /* ignore */ }

            _grabThread = null;

            lock (_lock)
            {
                _latest = null;
            }

            try
            {
                if (_device != null)
                {
                    try { _device.Parameters.ExecuteCommand("AcquisitionStop"); } catch { }
                    try { _device.StreamDisable(); } catch { }
                    try { _device.Disconnect(); } catch { }
                }
            }
            catch { /* ignore */ }

            try { if (_pipeline != null) { try { _pipeline.Stop(); } catch { } } } catch { }
            try { if (_stream != null) { try { _stream.Close(); } catch { } } } catch { }

            _pipeline = null;
            _stream = null;
            _device = null;

            StatusText = "相机未连接";
        }

        public bool TryGetLatestFrame(out CameraFrame frame)
        {
            lock (_lock)
            {
                if (_latest == null)
                {
                    frame = null!;
                    return false;
                }

                // 深拷贝像素数据，避免与取流线程共享同一数组
                var src = _latest;
                var copy = new byte[src.Pixels.Length];
                Buffer.BlockCopy(src.Pixels, 0, copy, 0, copy.Length);

                frame = new CameraFrame
                {
                    Pixels = copy,
                    Width = src.Width,
                    Height = src.Height,
                    Stride = src.Stride,
                    PixelFormat = src.PixelFormat,
                    TimestampUtc = src.TimestampUtc
                };
                return true;
            }
        }

        private unsafe void GrabLoop()
        {
            Debug.WriteLine("Ebus grab loop started");

            while (_running)
            {
                PvBuffer? buffer = null;
                try
                {
                    var pipeline = _pipeline;
                    if (pipeline == null || !pipeline.IsStarted)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    PvResult result = pipeline.RetrieveNextBuffer(ref buffer, 1000);
                    if (!result.IsOK)
                    {
                        if (buffer != null) pipeline.ReleaseBuffer(buffer);
                        continue;
                    }

                    if (buffer == null)
                        continue;

                    if (!buffer.OperationResult.IsOK || buffer.PayloadType != PvPayloadType.Image)
                    {
                        pipeline.ReleaseBuffer(buffer);
                        continue;
                    }

                    // 选择原始图像或转换后图像
                    PvImage pImage;
                    var pt = buffer.Image.PixelType;

                    if (pt == PvPixelType.BGR8 || pt == PvPixelType.RGB8 || pt == PvPixelType.Mono8)
                    {
                        pImage = buffer.Image;
                    }
                    else
                    {
                        SetConverterOutputToBgr8(_converter);
                        _converter.Convert(buffer, _converted);
                        pImage = _converted.Image;
                        pt = pImage.PixelType;
                    }

                    int w = (int)pImage.Width;
                    int h = (int)pImage.Height;

                    PixelFormat fmt;
                    int stride;
                    if (pt == PvPixelType.Mono8)
                    {
                        fmt = PixelFormats.Gray8;
                        stride = w;
                    }
                    else if (pt == PvPixelType.RGB8)
                    {
                        fmt = PixelFormats.Rgb24;
                        stride = w * 3;
                    }
                    else
                    {
                        // 默认按 BGR8
                        fmt = PixelFormats.Bgr24;
                        stride = w * 3;
                    }

                    int bytes = stride * h;
                    var managed = new byte[bytes];
                    Marshal.Copy((IntPtr)pImage.DataPointer, managed, 0, bytes);

                    var frame = new CameraFrame
                    {
                        Pixels = managed,
                        Width = w,
                        Height = h,
                        Stride = stride,
                        PixelFormat = fmt,
                        TimestampUtc = DateTime.UtcNow
                    };

                    lock (_lock)
                    {
                        _latest = frame;
                        _frameSeq++;
                    }

                    // 给 UI 推送做节流（默认 ~10Hz），避免 WPF 写像素过重
                    long nowTick = Stopwatch.GetTimestamp();
                    if (_lastUiPushTick == 0 || (nowTick - _lastUiPushTick) > Stopwatch.Frequency / 10)
                    {
                        _lastUiPushTick = nowTick;
                        try { FrameArrived?.Invoke(frame); } catch { /* ignore */ }
                    }

                    pipeline.ReleaseBuffer(buffer);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Ebus grab error: " + ex.Message);
                    try
                    {
                        if (buffer != null && _pipeline != null)
                            _pipeline.ReleaseBuffer(buffer);
                    }
                    catch { }

                    Thread.Sleep(80);
                }
            }

            Debug.WriteLine("Ebus grab loop stopped");
        }

        private static void AutoSelectColorPixelFormat(PvDevice device)
        {
            // 参考示例程序：遍历 PixelFormat 可用条目，优先 RGB8/BGR8，其次 Bayer
            var pf = device.Parameters.GetEnum("PixelFormat");
            long count = pf.Count();

            bool set = false;
            for (long i = 0; i < count; i++)
            {
                var entry = pf.GetEntryByIndex(i);
                if (!entry.IsAvailable) continue;

                string name = entry.ValueString ?? string.Empty;
                if (name.Contains("RGB", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("BGR", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Bayer", StringComparison.OrdinalIgnoreCase))
                {
                    pf.ValueString = name;
                    set = true;

                    if (name.Contains("RGB8", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("BGR8", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }

            if (!set)
                Debug.WriteLine("No color pixel format found (RGB/BGR/Bayer). Using camera default.");
        }

        /// <summary>
        /// 反射补丁：不同 eBUS 版本的 PvBufferConverter 输出像素属性名不一致。
        /// </summary>
        private static void SetConverterOutputToBgr8(PvBufferConverter converter)
        {
            if (converter == null) return;
            try
            {
                Type t = converter.GetType();
                string[] propNames = { "DestinationPixelFormat", "DestinationPixelType", "OutputPixelType", "DstPixelType", "OutputPixelFormat" };

                foreach (string name in propNames)
                {
                    PropertyInfo? p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanWrite)
                    {
                        p.SetValue(converter, PvPixelType.BGR8, null);
                        return;
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
