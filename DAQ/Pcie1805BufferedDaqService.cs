using Automation.BDaq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lanzhou_v1._0.DAQ
{
    /// <summary>
    /// PCIe-1805 AI 采集服务（InstantAiCtrl 轮询 + N 点滑动平均）
    ///
    /// 性能注意：
    /// - 原实现每个采样点都会分配数组（inst/avg），且在 1kHz 下会造成大量 GC，进而引发 UI 卡顿。
    /// - 本实现避免了采样循环内的托管分配，并把滑动平均从 O(N) 改为 O(1)（维护 sum / validCount）。
    ///
    /// 备注：
    /// - InstantAiCtrl 的采样率属于“尽力而为”，受 Windows 调度影响。
    /// - 若未来需要严格高频采集，请切换到 BufferedAiCtrl + DataReady 事件批量处理。
    /// </summary>
    public sealed class Pcie1805BufferedDaqService : IDisposable
    {
        private InstantAiCtrl? _ai;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private readonly object _lock = new();

        private DaqConfig? _cfg;

        // 预留最多 8 通道 ring；每通道固定分配 4096（避免运行期反复分配）
        private readonly double[][] _ring;
        private int _ringIndex;
        private int _cap; // 实际窗口长度（=AvgWindow，最大 4096）

        // O(1) 滑动平均状态
        private readonly double[] _sum = new double[8];
        private readonly int[] _validCount = new int[8];

        // 最新平均值（避免采样循环内分配）
        private double[] _latestChannels = Array.Empty<double>();
        private DaqSample _latest = new DaqSample { TimestampSeconds = 0, Channels = Array.Empty<double>() };

        private string _statusText = "DAQ: Not Initialized";

        public Pcie1805BufferedDaqService()
        {
            _ring = Enumerable.Range(0, 8).Select(_ => new double[4096]).ToArray();
        }

        public bool IsInitialized { get; private set; }
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        public string StatusText => _statusText;

        public void Initialize(int deviceId)
        {
            Stop();

            _ai?.Dispose();
            _ai = new InstantAiCtrl();

            _ai.SelectedDevice = new DeviceInformation(deviceId);

            // SelectedDevice 赋值不会抛错，但后续 Read 可能报错；这里主动做一次 Read 试探
            var test = 0.0;
            var err = _ai.Read(0, out test);

            if (err != ErrorCode.Success)
            {
                IsInitialized = false;
                _statusText = $"DAQ: Init Failed ({err})";
                return;
            }

            IsInitialized = true;
            _statusText = $"DAQ: Initialized (Dev={deviceId})";
        }

        public void Start(DaqConfig cfg)
        {
            if (!IsInitialized || _ai == null)
            {
                _statusText = "DAQ: Not Initialized";
                return;
            }

            Stop();

            cfg.Normalize();
            _cfg = cfg;

            // 平均窗口：严格等于 AvgWindow；上限 4096（与 ring 每通道容量一致）
            _cap = Math.Min(Math.Max(1, cfg.AvgWindow), 4096);

            // ring 初始化为 NaN（避免把默认 0 当成有效值）
            for (int ch = 0; ch < cfg.ChannelCount; ch++)
            {
                Array.Fill(_ring[ch], double.NaN, 0, _cap);
                _sum[ch] = 0.0;
                _validCount[ch] = 0;
            }

            _ringIndex = 0;

            // 预分配最新通道数组
            _latestChannels = new double[cfg.ChannelCount];
            lock (_lock)
            {
                _latest.TimestampSeconds = 0;
                _latest.Channels = _latestChannels;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _statusText = $"DAQ: Running (Dev={cfg.DeviceId}, SPS={cfg.SamplesPerSecond}, Avg={cfg.AvgWindow})";

            _loopTask = Task.Run(() => Loop(token), token);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts = null;

            try { _loopTask?.Wait(200); } catch { /* ignore */ }
            _loopTask = null;

            if (IsInitialized)
                _statusText = "DAQ: Initialized";
            else
                _statusText = "DAQ: Not Initialized";
        }

        public DaqSample GetLatestAveraged()
        {
            lock (_lock)
            {
                // 这里 clone 一次，避免 UI 线程拿到的引用被采样线程覆写
                return new DaqSample
                {
                    TimestampSeconds = _latest.TimestampSeconds,
                    Channels = (double[])_latest.Channels.Clone()
                };
            }
        }

        private void Loop(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            double lastTick = 0.0;

            while (!token.IsCancellationRequested)
            {
                var cfg = _cfg!;
                double dtTarget = 1.0 / Math.Max(1, cfg.SamplesPerSecond);

                // 软节拍：避免 SpinWait 吃满 CPU（采样率为“尽力而为”，优先保证 UI 流畅）
                double now = sw.Elapsed.TotalSeconds;
                double next = lastTick + dtTarget;
                if (now < next)
                {
                    int sleepMs = (int)Math.Ceiling((next - now) * 1000.0);
                    if (sleepMs <= 0) sleepMs = 1;
                    Thread.Sleep(sleepMs);
                    continue;
                }
                lastTick = now;

                try
                {
                    for (int ch = 0; ch < cfg.ChannelCount; ch++)
                    {
                        // InstantAiCtrl 的通道号是绝对通道号
                        double v;
                        var err = _ai!.Read(cfg.ChannelStart + ch, out v);
                        double inst = (err == ErrorCode.Success) ? v : double.NaN;

                        // 电压 -> 工程量（N）
                        double eng = double.IsNaN(inst) ? double.NaN : (inst * cfg.ScaleFactors[ch] + cfg.Offsets[ch]);

                        // 更新滑动窗口（长度=_cap）
                        double old = _ring[ch][_ringIndex];
                        if (!double.IsNaN(old) && !double.IsInfinity(old))
                        {
                            _sum[ch] -= old;
                            _validCount[ch] = Math.Max(0, _validCount[ch] - 1);
                        }

                        _ring[ch][_ringIndex] = eng;

                        if (!double.IsNaN(eng) && !double.IsInfinity(eng))
                        {
                            _sum[ch] += eng;
                            _validCount[ch]++;
                        }
                    }

                    _ringIndex = (_ringIndex + 1) % _cap;

                    lock (_lock)
                    {
                        for (int ch = 0; ch < cfg.ChannelCount; ch++)
                            _latestChannels[ch] = (_validCount[ch] > 0) ? (_sum[ch] / _validCount[ch]) : double.NaN;

                        _latest.TimestampSeconds = now;
                        _latest.Channels = _latestChannels;
                    }
                }
                catch (Exception ex)
                {
                    _statusText = "DAQ: Error - " + ex.Message;
                    Thread.Sleep(200);
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _ai?.Dispose();
            _ai = null;
        }
    }
}
