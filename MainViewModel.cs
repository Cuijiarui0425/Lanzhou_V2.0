using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows;
using System.Windows.Threading;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lanzhou_v1._0.PLC;
using Lanzhou_v1._0.DAQ;
using Lanzhou_v1._0.Sensors;
using Lanzhou_v1._0.Camera;
using Microsoft.Win32;

namespace Lanzhou_v1._0
{
    #region ========== 枚举定义区（业务状态/模式） ==========
    /// <summary>
    /// 试验模式枚举（UI 选择用）。
    /// 注意：当前 MVP 点表未包含“模式选择位”，因此该枚举目前只影响 UI 文本显示；
    /// 后续真点表若提供 Mode_xxx 线圈/寄存器，可在 SetMode() 中写 PLC 同步。
    /// </summary>
    public enum TrialMode
    {
        ManualP22Constant,
        ManualP24RecipIntermittent,
        ManualP26Step,
        LoadSystem,     // 新增
        Jog,
        AutoProgram1,
        AutoProgram2
    }


    /// <summary>
    /// 上位机运行状态（用于按钮禁用逻辑、状态栏显示等）。
    /// 关键点：此状态是“上位机侧”的状态机，不等同 PLC 内部状态机；
    /// PLC 内部状态建议用 StateCode/Running/Ready 等读回值体现。
    /// </summary>
    public enum RunState
    {
        Idle,
        Connected,
        Loading,
        Running,
        Stopping,
        Fault,
        EStop
    }
    #endregion

    /// <summary>
    /// MainViewModel：WPF MVVM 中的 ViewModel（主界面数据与命令的集合）。
    ///
    /// 主要职责：
    /// 1) UI 绑定数据：状态栏、设定值、实时值、曲线
    /// 2) UI 命令：连接/加载/启动/停止/急停/复位/导出/坐标轴切换/模式切换
    /// 3) PLC 读写：轮询读（1x/3x）+ 命令写（0x，脉冲/保持）
    /// 4) 点表加载：Config\pointmap_mvp.csv -> PlcPointMap（Key->Area/Address/Type）
    ///
    /// 设计原则（给维护者）：
    /// - UI 不直接写 Modbus 地址：一律通过点表 Key（例如 Cmd_Start）。
    /// - PLC 通讯细节封装在 ModbusPlcClient；点表解析封装在 PlcPointMap。
    /// - ViewModel 只做“业务编排”，避免散落协议细节。
    /// </summary>
    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        #region ========== INotifyPropertyChanged（WPF 绑定刷新机制） ==========
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<string, string>? UiMessageRequested;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        #endregion

        #region ========== 关键参数区（运行时可调/应固定的关键常量） ==========
        /// <summary>
        /// 命令脉冲宽度（ms）
        /// - Start/Stop/ResetAlarm 使用“脉冲式命令”时：写 1 -> 延时 -> 写 0
        /// - 若对方 PLC 采用“保持式命令”，可在各命令函数里切换为保持实现（代码已预留注释）。
        /// </summary>
        private const int CommandPulseMs = 150;

        /// <summary>
        /// Auto1（自动：恒速载荷）启动前的“参数下发 → 等待 PLC 扫描/锁存”延迟（ms）。
        ///
        /// 现场现象：HMI 下发 Auto1_TestLoad/Auto1_PhotoLoad 等参数后立刻给 Cmd_Auto1_Start 脉冲，
        /// PLC 可能尚未在其扫描周期内读取到新参数，导致出现“加载未执行就开始球盘运动/参数沿用上一轮”的问题。
        ///
        /// 解决：在 ApplyModeAndParametersAsync() 完成后，增加一个小延迟，再下发启动脉冲。
        /// </summary>
        private const int Auto1StartParamDelayMs = 350;

        /// <summary>
        /// Auto2（自动：变速）启动前的“参数下发 → 等待 PLC 扫描/锁存”延迟（ms）。
        ///
        /// 现场现象：若 HMI 下发参数后立刻给 Cmd_Auto2_Start 脉冲，PLC 可能尚未在程序周期内读取到新参数，
        /// 导致出现“加载未执行就开始球盘运动/参数沿用上一轮”的问题。
        ///
        /// 解决：在 ApplyModeAndParametersAsync() 完成后，增加一个小延迟，再下发启动脉冲。
        /// </summary>
        private const int Auto2StartParamDelayMs = 500;

        // —— PCIe-1805 载荷采集：为避免现场误操作，关键标定参数在代码内固化 ——
        private const int FixedDaqDeviceId = 1;
        private const double FixedLoadScaleNPerV = 4.9183;
        private const double FixedLoadOffsetN = 0.0;

        // 对数轴要求 X>0：给时间轴一个微小偏置，避免 t=0 触发对数坐标异常
        private const double TimeEpsSeconds = 1e-3;
        #endregion

        #region ========== 状态字段区（状态栏/按钮禁用逻辑/点表状态） ==========
        private TrialMode _mode = TrialMode.ManualP22Constant;
        private RunState _runState = RunState.Idle;
        // 说明：
        // - _localAlarmText：上位机本地异常/提示（通讯异常、参数校验失败等）
        // - _plcAlarmSummary：PLC/伺服侧报警（来自轮询读回 ErrorCode/AlarmCode）
        // - AlarmText 属性会按优先级合成显示
        private string _localAlarmText = "Alarm: None";
        private string _plcAlarmSummary = "";
        private string _alarmDetailText = "";

        private Lanzhou_v1._0.PLC.ServoAlarmCatalog? _servoAlarmCatalog;
        private Lanzhou_v1._0.PLC.PlcErrorCodeCatalog? _plcErrorCatalog;
        private bool _plcConnected;
        private bool _isAcquiring; // 采集开始后才允许写入曲线/显示速度
        private double _plcBallSpeedRaw = double.NaN;
        private double _plcDiskSpeedRaw = double.NaN;

        private readonly Pcie1805BufferedDaqService _daqService = new();
        // —— 串口（RS485 Modbus-RTU）采集：通道1=牵引力Ff，通道2=温度（按甲方要求解释）——
        private readonly StrainTransmitterService _strainService = new();
        private string _serialPortName = "COM6";
        private const int FixedSerialBaudRate = 115200;
        private const byte FixedSerialSlaveId = 1;
        private const int FixedSerialPollMs = 100;

        // —— 相机（eBUS SDK, PvDotNet）——
        private readonly EbusCameraService _cameraService = new();
        private WriteableBitmap? _cameraBitmap;
        private string _cameraStatusText = "相机未连接";
        private string _lastPhotoPath = "";

        // 自动程序1：拍照控制（按 PLC 的“拍照请求/完成”握手）
        private bool _auto1AutoPhotoEnabled = true;
        private string _auto1AutoPhotoStatusText = "Auto1 拍照：未触发";

        // PLC→HMI：拍照请求（4x 407 / UInt16），由 ReadMvpSnapshotAsync() 轮询写入
        private ushort _plcPhotoRequestRaw = 0;



// 拍照请求显示：用于“醒目提示”与联调取证（避免必须盯 Probe）
private bool _plcPhotoReqPrevActive = false;
private int _plcPhotoReqSeenCount = 0;
private DateTime? _plcPhotoReqLastSeenLocal = null;
        // Auto1 拍照握手内部状态
        private bool _auto1PhotoBusy = false;          // 正在拍照/保存/回写 Done
        private bool _auto1CameraDoneHolding = false;  // 已将 Done 置 1，等待 PLC 清零请求后复位
        private DateTime _auto1LastPhotoAttemptUtc = DateTime.MinValue;
        private const int Auto1PhotoRetryIntervalMs = 2000;

        private bool _daqEnabled;
        private int _daqDeviceId = FixedDaqDeviceId;
        private int _samplesPerSecond = 1000;
        private double _loadScaleNPerV = FixedLoadScaleNPerV;
        private double _loadOffsetN = FixedLoadOffsetN;

        // 串口载荷（COM6 通道1）标定：
        // Load(N) = (Raw + Offset) * Scale
        // Raw 为串口直接回传的数值（可能为计数/工程量未标定）
        private double _serialLoadOffset = 0.0;
        private double _serialLoadScale = 1.0;
        private double _serialLoadRaw = double.NaN;

        // —— 轻量配置持久化（用户侧常调参数）——
        // 说明：当前工程未使用 appsettings.json / Properties.Settings，故采用 config/ 下的 JSON 文件保存。
        private const string UserSettingsFileName = "user_settings.json";
        private DispatcherTimer? _userSettingsSaveTimer;
        private double _daqStartBaseSeconds = 0.0;
        private double _lastDaqPointTimeSeconds = -1.0;
        private PlcPointMap? _pointMap;
        private string _pointMapStatusText = "Map: Not Loaded";

        /// <summary>
        /// 状态栏：PLC 状态（含点表加载状态）
        /// </summary>
        public string PlcStatusText => (_plcConnected ? "PLC: Connected" : "PLC: Disconnected") + " | " + PointMapStatusText;

        /// <summary>
        /// 状态栏：DAQ 状态（当前 MVP 中以 bool 代替；后续接 PCIe 采集板后应替换为真实状态）
        /// </summary>
        public string DaqStatusText => _daqService.StatusText;

        /// <summary>
        /// 状态栏：串口采集状态（Ff/温度）
        /// </summary>
        public string SerialStatusText => _strainService.StatusText;

        /// <summary>
        /// 状态栏：相机状态（USB/网口）
        /// </summary>
        public string CameraStatusText => _cameraStatusText;

        /// <summary>
        /// 相机预览图像（WPF Image 绑定）
        /// </summary>
        public ImageSource? CameraPreviewImage => _cameraBitmap;

        /// <summary>
        /// 最近一次保存的照片路径（显示用）
        /// </summary>
        public string LastPhotoPath => string.IsNullOrWhiteSpace(_lastPhotoPath) ? "LastPhoto: --" : _lastPhotoPath;



/// <summary>
/// PLC→HMI：拍照请求原始值（4x407 / UInt16）。用于界面醒目显示。
/// </summary>
public ushort PlcPhotoRequestRaw => _plcPhotoRequestRaw;

/// <summary>
/// PLC→HMI：拍照请求是否处于激活状态（4x407 != 0）。
/// </summary>
public bool IsPlcPhotoRequestActive => _plcPhotoRequestRaw != 0;

/// <summary>
/// 右侧醒目提示用：显示 4x407 的当前值。
/// </summary>
public string PlcPhotoRequestRawText => $"4x407={_plcPhotoRequestRaw}";

/// <summary>
/// 右侧醒目提示用：最近一次“看到请求置位”的次数与时间（本地时间）。
/// </summary>
public string PlcPhotoReqSeenInfo
{
    get
    {
        if (_plcPhotoReqSeenCount <= 0) return "Seen=0";
        var t = _plcPhotoReqLastSeenLocal.HasValue ? _plcPhotoReqLastSeenLocal.Value.ToString("HH:mm:ss") : "--:--:--";
        return $"Seen={_plcPhotoReqSeenCount} @ {t}";
    }
}
        /// <summary>
        /// 自动程序1：是否启用“PLC 拍照请求(4x407)=1 -> HMI 拍照并保存 -> 回写 Done(0x319)=1”的握手机制。
        /// </summary>
        public bool Auto1AutoPhotoEnabled
        {
            get => _auto1AutoPhotoEnabled;
            set
            {
                if (_auto1AutoPhotoEnabled == value) return;
                _auto1AutoPhotoEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
            }
        }

        public string Auto1AutoPhotoStatusText => _auto1AutoPhotoStatusText;

        /// <summary>
        /// 串口名称（默认 COM6）
        /// </summary>
        public string SerialPortName
        {
            get => _serialPortName;
            set
            {
                if (_serialPortName == value) return;
                _serialPortName = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 状态栏：当前模式文本
        /// </summary>
        public string CurrentModeText => $"Mode: {_mode}";

        /// <summary>
        /// 参数输入区标题：随模式变化（用于将“当前模式参数”替换为具体模式名称）。
        /// </summary>
        public string CurrentModeParamHeader => _mode switch
        {
            TrialMode.LoadSystem => "加载系统参数",
            TrialMode.Jog => "点动模式参数",
            TrialMode.ManualP22Constant => "恒速模式参数",
            TrialMode.ManualP24RecipIntermittent => "往复/间歇模式参数",
            TrialMode.ManualP26Step => "阶梯模式参数",
            TrialMode.AutoProgram1 => "自动：恒速载荷参数",
            TrialMode.AutoProgram2 => "自动：变速参数",
            _ => "当前模式参数"
        };

        /// <summary>
        /// 当前模式（用于 XAML DataTrigger 选择不同参数面板）。
        /// </summary>
        public TrialMode CurrentMode => _mode;

        /// <summary>
        /// Auto1（自动：恒定/恒速载荷）模式判定。
        /// 仅用于 UI 显示控制：使“PLC 拍照请求（4x407）指示灯”仅在 Auto1 模式出现。
        /// </summary>
        public bool IsAutoProgram1Mode => _mode == TrialMode.AutoProgram1;

        /// <summary>
        /// 状态栏：运行状态文本
        /// </summary>
        public string RunStateText => $"State: {_runState}";

        /// <summary>
        /// 状态栏：报警文本（上位机侧）
        /// 注意：PLC 报警建议以 AlarmActive/AlarmCode 读回值为准；此处更多是 UI 提示。
        /// </summary>
        /// <summary>
        /// 报警摘要（状态栏/信息区显示）：
        /// 优先级：EStop > 本地异常 > PLC/伺服报警
        /// </summary>
        public string AlarmText
        {
            get
            {
                if (_runState == RunState.EStop) return "Alarm: E-STOP";

                if (!string.IsNullOrWhiteSpace(_localAlarmText) && _localAlarmText != "Alarm: None")
                    return _localAlarmText;

                if (!string.IsNullOrWhiteSpace(_plcAlarmSummary))
                    return _plcAlarmSummary;

                return "Alarm: None";
            }
        }

        /// <summary>
        /// 报警详情（多行文本，用于联调/定位）：
        /// - PLC 自定义错误码（500~）
        /// - 伺服 DWord 报警码（0x****）及原因/处理建议
        /// - 结合当前状态位（限位/完成/失败）
        /// </summary>
        public string AlarmDetailText => _alarmDetailText;

        /// <summary>
        /// 点表加载状态文本
        /// </summary>
        public string PointMapStatusText => _pointMapStatusText;

        /// <summary>
        /// DAQ 设备 ID（DAQNavi 设备号，通常为 0 或 1；以研华设备管理器/DAQNavi 为准）
        /// </summary>
        public string DaqDeviceId
        {
            get => _daqDeviceId.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, out var v) && v >= 0 && v <= 99)
                {
                    _daqDeviceId = v;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// DAQ 采样率（Samples Per Second, 每秒采样点数/每通道）
        /// </summary>
        public string SamplesPerSecond
        {
            get => _samplesPerSecond.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, out var v) && v >= 1 && v <= 200000)
                {
                    _samplesPerSecond = v;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 串口载荷（COM6 通道1）偏移量：
        /// Load(N) = (Raw + Offset) * Scale
        /// </summary>
        public string SerialLoadOffset
        {
            get => _serialLoadOffset.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _serialLoadOffset = v;
                    OnPropertyChanged();
                    RequestSaveUserSettings();
                }
            }
        }

        /// <summary>
        /// 串口载荷（COM6 通道1）缩放倍数：
        /// Load(N) = (Raw + Offset) * Scale
        /// </summary>
        public string SerialLoadScale
        {
            get => _serialLoadScale.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _serialLoadScale = v;
                    OnPropertyChanged();
                    RequestSaveUserSettings();
                }
            }
        }

        /// <summary>
        /// 载荷标定系数：N/V
        /// Load(N) = Voltage(V) * LoadScaleNPerV + LoadOffsetN
        /// </summary>
        public string LoadScaleNPerV
        {
            get => _loadScaleNPerV.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _loadScaleNPerV = v;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 载荷标定偏置：N
        /// </summary>
        public string LoadOffsetN
        {
            get => _loadOffsetN.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _loadOffsetN = v;
                    OnPropertyChanged();
                }
            }
        }
        #endregion

        #region ========== PLC 通讯对象与轮询控制区（关键） ==========
        /// <summary>
        /// PLC 客户端（Modbus TCP）：
        /// - 读：ReadMvpSnapshotAsync（读取 DI/IR）
        /// - 写：WriteCoilAsync / PulseCoilAsync / WriteHoldingRegistersAsync / WriteFloat32HoldingRegistersAsync
        /// </summary>
        private readonly Lanzhou_v1._0.PLC.ModbusPlcClient _plcClient = new Lanzhou_v1._0.PLC.ModbusPlcClient();

        /// <summary>
        /// PLC 轮询 Task 取消令牌
        /// </summary>
        private System.Threading.CancellationTokenSource? _plcPollCts;

        
        // PLC 轮询线程 -> UI 更新的“合并开关”（避免 BeginInvoke 排队导致卡顿）
        private int _plcUiUpdatePending = 0;
/// <summary>
        /// PLC 连接参数（IP/端口/站号）
        /// 关键变量：工控机与 PLC 联调时需与真 PLC 配置一致。
        /// </summary>
        // 默认 PLC IP（可在界面中修改）
        public string PlcIp { get; set; } = "192.168.1.10";
        public int PlcPort { get; set; } = 502;
        public byte PlcUnitId { get; set; } = 1;

        /// <summary>
        /// 顶栏 Probe 文本：用于显示“轮询读回的 DI/IR 值”，便于联调排障。
        /// </summary>
        private string _plcProbeText = "Probe: Not Started";
        public string PlcProbeText => _plcProbeText;


        // ========= 加载系统：按住加载/卸载的“复位型按钮”逻辑（自动停机判据） =========
        private bool _pressLimit; // 来自 PLC 轮询（St_PressLimit）
        private bool _pressLoadHolding;
        private bool _pressUnloadHolding;
        private bool _pressLimitAtStartLoad;
        private bool _pressLimitSeenFalseDuringLoad;
        private bool _pressStopping;

        // ========= 加载系统：点击加载/卸载（PLC 完成信号 404/405） =========
        private bool _pressOpInProgress;
        private System.Threading.CancellationTokenSource? _pressOpCts;
        private bool _pressStartHolding; // Cmd_PressStart(MX630.0/315) 保持位

        private bool _plcMotionDone;
        private bool _plcMotionFail;
        private bool _plcMotionDonePrev;
        private bool _plcMotionFailPrev;
        private bool _plcMotionDoneLatched;
        private bool _plcMotionFailLatched;

        // ========= PLC 快照（状态位缓存，用于启动前置条件判断） =========
        private bool _plcBallEnableDone;
        private bool _plcDiskEnableDone;
        private bool _plcPressEnableDone;
        private bool _plcAuto2SingleDone;
        private bool _plcAuto2SingleDonePrev;
        private float _plcLoadAlarmLimit;

        // ========= 传感器原始量缓存（用于回写 PLC） =========
        // traction/拉压力（N）：来自应变片通道（ffRaw）
        private double _tractionForceN = double.NaN;

        // ========= 传感器回写 PLC 节流（避免 20Hz 高频写导致总线拥塞） =========
        private readonly System.Threading.SemaphoreSlim _feedbackWriteLock = new System.Threading.SemaphoreSlim(1, 1);
        private double _lastFeedbackWriteSeconds = -999.0;
        private const double FeedbackWritePeriodSeconds = 0.20; // 5 Hz
        private bool _feedbackTagsReady = true;

        // ========= 手动功能：载荷设定“由加载系统带入” =========
        // 说明：
        // - 项目要求：点动/恒速/往复(间歇)必须在“加载系统”加载到目标载荷后再运动。
        // - UI 侧：在上述三种运动模式参数区中，设定载荷显示“最近一次成功加载到的载荷值”，并禁用编辑。
        // - 为避免“加载后又改了 LoadNSet 但实际上并未再次加载”带来的歧义，这里增加一个锁存值。
        private bool _manualAppliedLoadValid;
        private double _manualAppliedLoadN;

        #endregion

        #region ========== 状态机辅助函数区（关键） ==========
        /// <summary>
        /// 设置上位机运行状态（会触发按钮禁用刷新）
        /// </summary>
        private void SetRunState(RunState s)
        {
            _runState = s;
            OnPropertyChanged(nameof(RunStateText));
            OnPropertyChanged(nameof(AlarmText));
            RefreshCommandState();
        }

        /// <summary>
        /// 设置 UI 当前模式（当前仅用于显示；后续可扩展为写 PLC 模式位）
        /// </summary>
        private void SetMode(TrialMode m)
        {
            if (_mode == TrialMode.LoadSystem && m != TrialMode.LoadSystem)
            {
                // 离开“加载系统”模式时，确保按住类线圈全部释放（避免误保持）
                _pressLoadHolding = false;
                _pressUnloadHolding = false;
                _pressStopping = false;
                if (_plcConnected)
                    StopLoadSystemOutputsSilently();
            }

            _mode = m;
            OnPropertyChanged(nameof(CurrentModeText));
            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CurrentModeParamHeader));
            OnPropertyChanged(nameof(IsAutoProgram1Mode));

            // 若真点表提供模式选择位，可在此处写 PLC 同步（例如 Mode_Constant=1，其余=0）。

            // 触发按钮 CanExecute 重新评估（例如：Start 是否因加载锁存而置灰）
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// Start 按钮是否可用：
        /// - 恒速(P2.2)、往复/间歇(P2.4) 仍要求先在【加载系统】完成“加载到设定载荷”锁存；
        /// - 点动(Jog) 与加载系统分离，不依赖该锁存；
        /// - 其它模式按原逻辑。
        /// </summary>
        private bool CanStartCurrentMode()
        {
            if (!_plcConnected) return false;
            if (!(_runState == RunState.Connected || _runState == RunState.Loading)) return false;

            bool needManualLoad = _mode == TrialMode.ManualP22Constant || _mode == TrialMode.ManualP24RecipIntermittent;
            if (needManualLoad)
                return _manualAppliedLoadValid;

            return true;
        }
        #endregion

        #region ========== 设定值字段区（参数输入区绑定） ==========
        /// <summary>
        /// 设定值（UI 输入框绑定），用于下发 PLC 参数（4x）以及模拟源。
        /// 备注：球速/盘速单位已按会议要求统一为 mm/s。
        /// </summary>
        private double _ballSpeedSet = 100;   // mm/s
        private double _diskSpeedSet = 100;   // mm/s
        private double _workTimeSet = 10;    // s（默认时间：P26 表格初值等）

        // P22：恒速模式下球/盘分别计时（按点表：P22_BallTime / P22_DiskTime）
        private double _p22BallWorkTimeSet = 10; // s
        private double _p22DiskWorkTimeSet = 10; // s

        // P24：两段速度/时间（可用于“往复/间歇”——例如第二段速度设为 0 即为停歇）
        private double _ballSpeed2Set = 0;   // mm/s
        private double _diskSpeed2Set = 0;   // mm/s
        private double _stageTime1Set = 5;   // s
        private double _stageTime2Set = 5;   // s

        private double _loadNSet = 30;

        // 点动/加载系统参数
        private double _ballJogSpeedSet = 10;   // mm/s
        private double _diskJogSpeedSet = 10;   // mm/s
        private double _pressJogSpeedSet = 2;   // mm/s（加载系统：下压点动速度）


        // 点动方向选择：球/盘均用“勾选方向”互斥。
        // 实际点动由运行区“按住点动”按钮置位/复位保持线圈（Cmd_*Jog*Hold）。
        private bool _ballJogForward = true;
        private bool _ballJogReverse = false;
        private bool _diskJogForward = true;
        private bool _diskJogReverse = false;
        private bool _pressJogForward = true;
        private bool _pressJogReverse = false;
        private int _avgPoints = 10;
        private string _notes = "";

        public string BallSpeedSet
        {
            get => _ballSpeedSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _ballSpeedSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string DiskSpeedSet
        {
            get => _diskSpeedSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _diskSpeedSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string BallWorkTimeSet
        {
            get => _p22BallWorkTimeSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _p22BallWorkTimeSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string DiskWorkTimeSet
        {
            get => _p22DiskWorkTimeSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _p22DiskWorkTimeSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string WorkTimeSet
        {
            get => _workTimeSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _workTimeSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string BallSpeed2Set
        {
            get => _ballSpeed2Set.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _ballSpeed2Set = v;
                    OnPropertyChanged();
                }
            }
        }

        public string DiskSpeed2Set
        {
            get => _diskSpeed2Set.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _diskSpeed2Set = v;
                    OnPropertyChanged();
                }
            }
        }

        public string StageTime1Set
        {
            get => _stageTime1Set.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _stageTime1Set = v;
                    OnPropertyChanged();
                }
            }
        }

        public string StageTime2Set
        {
            get => _stageTime2Set.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _stageTime2Set = v;
                    OnPropertyChanged();
                }
            }
        }

        // ----------- 点动/自动通用加减速度参数（轴控区） -----------
        private double _AxisNormalSpeed = 3000; //额定转速3000rpm
        private double _AxisNormalVelocity = 2730; //额定线速度2730mm/s

        private double _jogAccSet = 27300;   // mm/s^2
        private double _jogDecSet = 27300;   // mm/s^2
        private double _autoAccSet = 27300;  // mm/s^2
        private double _autoDecSet = 27300;  // mm/s^2

        //private static readonly double _AxisNormalSpeed = 3000;
        //private static readonly double _AxisNormalVelocity = 54.6 * _AxisNormalSpeed / 60;

        //private static readonly double _jogAccSet = _AxisNormalVelocity * 10;
        //private static readonly double _jogDecSet = _AxisNormalVelocity * 10;
        //private static readonly double _autoAccSet = _AxisNormalVelocity * 10;
        //private static readonly double _autoDecSet = _AxisNormalVelocity * 10;

        public string JogAccSet
        {
            get => _jogAccSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _jogAccSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string JogDecSet
        {
            get => _jogDecSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _jogDecSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string AutoAccSet
        {
            get => _autoAccSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _autoAccSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string AutoDecSet
        {
            get => _autoDecSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _autoDecSet = v;
                    OnPropertyChanged();
                }
            }
        }

        // ----------- P26 阶梯（1~16 段）参数：使用表格输入，避免 16 组文本框冗余 -----------
        private int _p26StepCount = 4;
        public string P26StepCount
        {
            get => _p26StepCount.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    _p26StepCount = Math.Max(1, Math.Min(16, v));
                    UpdateP26ActiveFlags();
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<P26StepRow> P26Steps { get; } = new ObservableCollection<P26StepRow>();

        /// <summary>
        /// 将“阶梯段数 N”映射到每行的可编辑状态：前 N 行可编辑，其余行置灰并禁用。
        /// </summary>
        private void UpdateP26ActiveFlags()
        {
            int n = Math.Max(1, Math.Min(16, _p26StepCount));
            for (int i = 0; i < P26Steps.Count; i++)
            {
                P26Steps[i].IsActive = (i + 1) <= n;
            }
        }

        // ================= P26（阶梯模式）快捷输入实现 =================
        // 说明：
        // - 阶梯段数由 P26StepCount（1~16）控制
        // - DataGrid 仅输入 4 列：球速、盘速、时间、载荷
        private void P26CopyFirstRowToN()
        {
            int n = Math.Max(1, Math.Min(16, _p26StepCount));
            if (P26Steps.Count == 0) return;

            var first = P26Steps[0];
            for (int i = 1; i < n && i < P26Steps.Count; i++)
            {
                P26Steps[i].BallSpeed = first.BallSpeed;
                P26Steps[i].DiskSpeed = first.DiskSpeed;
                P26Steps[i].TimeSeconds = first.TimeSeconds;
                P26Steps[i].LoadN = first.LoadN;
            }
        }

        private void P26LinearGenerate()
        {
            int n = Math.Max(1, Math.Min(16, _p26StepCount));
            if (P26Steps.Count == 0 || n < 2) return;

            var a = P26Steps[0];
            var b = P26Steps[Math.Min(n - 1, P26Steps.Count - 1)];

            double aBall = a.BallSpeed, bBall = b.BallSpeed;
            double aDisk = a.DiskSpeed, bDisk = b.DiskSpeed;
            double aTime = a.TimeSeconds, bTime = b.TimeSeconds;
            double aLoad = a.LoadN, bLoad = b.LoadN;

            for (int i = 0; i < n && i < P26Steps.Count; i++)
            {
                double r = (n == 1) ? 0.0 : (double)i / (n - 1);
                P26Steps[i].BallSpeed = aBall + (bBall - aBall) * r;
                P26Steps[i].DiskSpeed = aDisk + (bDisk - aDisk) * r;
                P26Steps[i].TimeSeconds = aTime + (bTime - aTime) * r;
                P26Steps[i].LoadN = aLoad + (bLoad - aLoad) * r;
            }
        }

        private void P26PasteFromClipboard()
        {
            try
            {
                string clip = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(clip))
                {
                    UiMessageRequested?.Invoke("提示", "剪贴板为空。请从 Excel 复制 4 列：球速、盘速、时间、载荷。");
                    return;
                }

                var lines = clip.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                int row = 0;

                foreach (var raw in lines)
                {
                    if (row >= P26Steps.Count) break;

                    var line = (raw ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // 支持 tab/逗号/分号/空格分隔
                    var parts = line.Split(new[] { '	', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;

                    if (!TryParseDouble(parts[0], out var vBall)) continue;
                    if (!TryParseDouble(parts[1], out var vDisk)) continue;
                    if (!TryParseDouble(parts[2], out var vTime)) continue;
                    if (!TryParseDouble(parts[3], out var vLoad)) continue;

                    P26Steps[row].BallSpeed = vBall;
                    P26Steps[row].DiskSpeed = vDisk;
                    P26Steps[row].TimeSeconds = vTime;
                    P26Steps[row].LoadN = vLoad;

                    row++;
                }

                if (row > 0)
                {
                    _p26StepCount = Math.Max(1, Math.Min(16, row));
                    UpdateP26ActiveFlags();
                    OnPropertyChanged(nameof(P26StepCount));
                }
            }
            catch (Exception ex)
            {
                UiMessageRequested?.Invoke("粘贴失败", ex.Message);
            }
        }

        private void P26ClearTail()
        {
            int n = Math.Max(1, Math.Min(16, _p26StepCount));
            for (int i = n; i < P26Steps.Count; i++)
            {
                P26Steps[i].BallSpeed = 0.0;
                P26Steps[i].DiskSpeed = 0.0;
                P26Steps[i].TimeSeconds = 0.0;
                P26Steps[i].LoadN = 0.0;
            }
        }

        /// <summary>
        /// 校验阶梯模式前 N 段参数（仅在“启动/下发”时触发），避免向 PLC 下发明显非法的数据。
        /// 规则（尽量保守）：
        /// - 球/盘速度：必须为有限数值（允许正负，方向由 PLC 侧解释）
        /// - 时间：必须 > 0
        /// - 载荷：必须 >= 0
        /// </summary>
        private string? ValidateP26TableForStart(int n)
        {
            n = Math.Max(1, Math.Min(16, n));
            for (int i = 1; i <= n; i++)
            {
                if (i - 1 >= P26Steps.Count) break;
                var row = P26Steps[i - 1];

                // 先利用行内校验信息（若存在）
                if (row.HasErrors)
                {
                    // 取一个最关键的提示（避免把所有字段都堆给用户）
                    var msg = row.GetErrors(nameof(P26StepRow.TimeSeconds)).Cast<string?>().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
                           ?? row.GetErrors(nameof(P26StepRow.LoadN)).Cast<string?>().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
                           ?? row.GetErrors(nameof(P26StepRow.BallSpeed)).Cast<string?>().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
                           ?? row.GetErrors(nameof(P26StepRow.DiskSpeed)).Cast<string?>().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
                           ?? "存在非法输入";
                    return $"第 {i} 段参数无效：{msg}";
                }

                // 兜底：即便行内错误未刷新，也在启动前做一次硬校验
                if (double.IsNaN(row.BallSpeed) || double.IsInfinity(row.BallSpeed)) return $"第 {i} 段：球速必须为有限数值";
                if (double.IsNaN(row.DiskSpeed) || double.IsInfinity(row.DiskSpeed)) return $"第 {i} 段：盘速必须为有限数值";
                if (double.IsNaN(row.TimeSeconds) || double.IsInfinity(row.TimeSeconds) || row.TimeSeconds <= 0) return $"第 {i} 段：时间必须 > 0";
                if (double.IsNaN(row.LoadN) || double.IsInfinity(row.LoadN) || row.LoadN < 0) return $"第 {i} 段：载荷必须 ≥ 0";
            }
            return null;
        }

        private static bool TryParseDouble(string s, out double v)
        {
            s = (s ?? string.Empty).Trim();

            // 兼容欧式小数逗号：若包含逗号且不包含点，则认为逗号是小数点
            if (s.Contains(',') && !s.Contains('.'))
                s = s.Replace(',', '.');
            else if (s.Count(c => c == ',') >= 1 && s.Contains('.'))
                s = s.Replace(",", ""); // 删除千分位

            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)
                   || double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out v);
        }


        // ----------- 自动程序 1 / 2 参数（按交互表） -----------
        private int _auto1CycleCount = 2;
        private double _auto1TestLoad = 30;
        private double _auto1PhotoLoad = 20;
        private double _auto1WorkTime = 10;
        private double _auto1BallSpeed = 100;
        private double _auto1DiskSpeed = 100;

        public string Auto1CycleCount
        {
            get => _auto1CycleCount.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    _auto1CycleCount = Math.Max(1, v);
                    OnPropertyChanged();
                }
            }
        }

        public string Auto1TestLoad
        {
            get => _auto1TestLoad.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _auto1TestLoad = v;
                    OnPropertyChanged();
                }
            }
        }

        public string Auto1PhotoLoad
        {
            get => _auto1PhotoLoad.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _auto1PhotoLoad = v;
                    OnPropertyChanged();
                }
            }
        }

        public string Auto1WorkTime
        {
            get => _auto1WorkTime.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _auto1WorkTime = v;
                    OnPropertyChanged();
                }
            }
        }

        public string Auto1BallSpeed
        {
            get => _auto1BallSpeed.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _auto1BallSpeed = v;
                    OnPropertyChanged();
                }
            }
        }

        public string Auto1DiskSpeed
        {
            get => _auto1DiskSpeed.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _auto1DiskSpeed = v;
                    OnPropertyChanged();
                }
            }
        }

        
// ======== 自动程序2（变速/多点）参数 ========
private int _auto2CycleCount = 4;
private double _auto2TestLoad = 30;
private double _auto2StageTimeSeconds = 10;   // UI 输入单位：秒；写 PLC 时转换为 Int32 毫秒
private double _auto2DiskMaxSpeed = 400;       // mm/s
private double _auto2DiskMinSpeed = 100;       // mm/s
private double _auto2Srr = 0.5;               // SRR

public string Auto2CycleCount
{
    get => _auto2CycleCount.ToString(CultureInfo.InvariantCulture);
    set
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            _auto2CycleCount = Math.Max(1, v);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Auto2PointProgressText));
        }
    }
}

public string Auto2TestLoad
{
    get => _auto2TestLoad.ToString(CultureInfo.InvariantCulture);
    set
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            _auto2TestLoad = v;
            OnPropertyChanged();
        }
    }
}

public string Auto2StageTime
{
    get => _auto2StageTimeSeconds.ToString(CultureInfo.InvariantCulture);
    set
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            _auto2StageTimeSeconds = Math.Max(0.0, v);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Auto2CurrentTargetText));
        }
    }
}

public string Auto2DiskMaxSpeed
{
    get => _auto2DiskMaxSpeed.ToString(CultureInfo.InvariantCulture);
    set
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            _auto2DiskMaxSpeed = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Auto2CurrentTargetText));
        }
    }
}

public string Auto2DiskMinSpeed
{
    get => _auto2DiskMinSpeed.ToString(CultureInfo.InvariantCulture);
    set
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            _auto2DiskMinSpeed = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Auto2CurrentTargetText));
        }
    }
}

public string Auto2Srr
{
    get => _auto2Srr.ToString(CultureInfo.InvariantCulture);
    set
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            _auto2Srr = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Auto2CurrentTargetText));
        }
    }
}

// ======== 自动程序2（变速/多点）只读显示（放在红框区域） ========

private enum Auto2RunPhase { Idle, Loading, WaitStable, Recording, WaitingAckClear, UnloadingWait, Finished }

// —— Auto2 状态推断与握手参数（可按现场需要微调）——
// 进入“循环测试”判据：速度需明显高于 0，避免把加载阶段（速度≈0）误当作循环
private const double Auto2EnterRel = 0.10;      // 相对阈值：10% of max
private const double Auto2EnterAbsDisk = 20.0;  // mm/s
private const double Auto2EnterAbsBall = 20.0;  // mm/s

// 稳定判据：在最近 Auto2StableWindowSeconds 内波动范围小于 band
private const double Auto2StableWindowSeconds = 0.5; // s（用于计算 max-min）
private const double Auto2StableRelBand = 0.4;      // 3%
private const double Auto2StableAbsBand = 100.0;      // mm/s
private const double Auto2SettleSeconds = 0.1;       // 连续稳定保持时间（s）

// 兼容：保留“达速容差”常量（用于显示/备用），但 Auto2 主流程不再依赖“目标速度达速”
private const double Auto2ReachRelTol = 0.05;  // 5% 相对误差（备用）
private const double Auto2ReachAbsTol = 10.0;  // mm/s 绝对误差下限（备用）

private const double Auto2MinEffectiveSeconds = 0.2; // 有效窗太短则走兜底统计

// DataDone(322) 握手：置 1 并保持，直到 SingleDone 清 0，再写回 0
private const double Auto2AckMinHoldSeconds = 0.25;     // s
private const double Auto2AckResendPeriodSeconds = 0.8; // s（防止写入丢包，周期性重写 1）

// 启动按钮：先下发参数再启动。为避免 PLC 尚未在扫描周期内读到参数就进入运动，
// 在参数写完后增加一个短延时，再下发启动脉冲（仅 Auto2）。
private const int Auto2StartAfterParamDelayMs = 500; // ms

// 卸载完成判据：球/盘速度连续低于阈值一段时间
private const double Auto2UnloadingDetectSeconds = 1.0;   // s
private const double Auto2UnloadingMaxWaitSeconds = 30.0; // s（超时后尝试 TotalStop 兜底）

// —— Auto2 运行态字段 ——
private Auto2RunPhase _auto2Phase = Auto2RunPhase.Idle;
private readonly System.Collections.Generic.Queue<(double t, double disk, double ball)> _auto2SpeedHist = new();
private double _auto2StableStartSeconds = double.NaN;
private double _auto2RecordingOpenSeconds = double.NaN;
private bool _auto2WindowFull = false;

private bool _auto2RunActive = false;
private bool _auto2RunFrozen = false;
private int _auto2CurrentPointIndex = 0; // 1-based
private List<(double disk, double ball)> _auto2Targets = new List<(double disk, double ball)>();

private double _auto2PointStartSeconds = double.NaN; // 统计窗开始（稳定后）
private double _auto2LastAccumSeconds = double.NaN;
private double _auto2AccumSeconds = 0.0;

private double _auto2ForceIntegral = 0.0;
private double _auto2LoadIntegral = 0.0;
private double _auto2MuIntegral = 0.0;
private double _auto2MuSeconds = 0.0;
private double _auto2BallIntegral = 0.0;
private double _auto2DiskIntegral = 0.0;

private double _auto2PointAvgForceN = double.NaN;
private double _auto2PointAvgMu = double.NaN;

// 握手相关（322）
private bool _auto2AckActive = false;
private double _auto2AckStartSeconds = double.NaN;
private double _auto2AckLastWriteSeconds = double.NaN;

// 卸载判定
private double _auto2UnloadWaitStartSeconds = double.NaN;
private double _auto2UnloadStableStartSeconds = double.NaN;

// 速度阈值缓存（根据输入参数计算）
private double _auto2EnterDiskThreshold = Auto2EnterAbsDisk;
private double _auto2EnterBallThreshold = Auto2EnterAbsBall;

private string _auto2StateText = "Auto2：未开始";

public string Auto2StateText => _auto2StateText;

public string Auto2PointProgressText
{
    get
    {
        if (!_auto2RunActive && !_auto2RunFrozen) return $"点位：--/{_auto2CycleCount}";
        return $"点位：{Math.Max(1, _auto2CurrentPointIndex)}/{_auto2CycleCount}";
    }
}

public string Auto2CurrentTargetText
{
    get
    {
        if ((!_auto2RunActive && !_auto2RunFrozen) || _auto2CurrentPointIndex <= 0 || _auto2CurrentPointIndex > _auto2Targets.Count)
            return "目标：--";
        var (d, b) = _auto2Targets[_auto2CurrentPointIndex - 1];
        return $"目标盘速={d:F3} mm/s  目标球速={b:F3} mm/s  SRR={_auto2Srr:F3}  单点={_auto2StageTimeSeconds:F1}s";
    }
}

public string Auto2EffectiveTimeText
{
    get
    {
        if (!_auto2RunActive && !_auto2RunFrozen) return "有效时间：--";
        return $"有效时间：{_auto2AccumSeconds:F2} s";
    }
}

public string Auto2AvgForceText
{
    get
    {
        if (!_auto2RunActive && !_auto2RunFrozen) return "本点平均拉压力：--";
        if (double.IsNaN(_auto2PointAvgForceN)) return "本点平均拉压力：--";
        return $"本点平均拉压力：{_auto2PointAvgForceN:F3} N";
    }
}

public string Auto2AvgMuText
{
    get
    {
        if (!_auto2RunActive && !_auto2RunFrozen) return "本点平均牵引力系数：--";
        if (double.IsNaN(_auto2PointAvgMu)) return "本点平均牵引力系数：--";
        return $"本点平均牵引力系数：{_auto2PointAvgMu:F5}";
    }
}

        public string LoadNSet
        {
            get => _loadNSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _loadNSet = v;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ManualLoadSetForMotion));
                }
            }
        }

        /// <summary>
        /// 点动/恒速/往复(间歇)等运动模式显示用“设定载荷”。
        /// - 若已在加载系统中“成功加载到设定值”，则锁存并显示该值；
        /// - 否则显示当前 LoadNSet（提示用户先去加载系统加载）。
        /// </summary>
        public string ManualLoadSetForMotion
            => (_manualAppliedLoadValid ? _manualAppliedLoadN : _loadNSet).ToString(CultureInfo.InvariantCulture);


        public string PressJogSpeedSet
        {
            get => _pressJogSpeedSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _pressJogSpeedSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string BallJogSpeedSet
        {
            get => _ballJogSpeedSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _ballJogSpeedSet = v;
                    OnPropertyChanged();
                }
            }
        }

        public string DiskJogSpeedSet
        {
            get => _diskJogSpeedSet.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _diskJogSpeedSet = v;
                    OnPropertyChanged();
                }
            }
        }


        public bool BallJogForward
        {
            get => _ballJogForward;
            set
            {
                if (_ballJogForward != value)
                {
                    _ballJogForward = value;
                    if (value) _ballJogReverse = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BallJogReverse));
                }
            }
        }

        public bool BallJogReverse
        {
            get => _ballJogReverse;
            set
            {
                if (_ballJogReverse != value)
                {
                    _ballJogReverse = value;
                    if (value) _ballJogForward = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BallJogForward));
                }
            }
        }
        public bool DiskJogForward
        {
            get => _diskJogForward;
            set
            {
                if (_diskJogForward != value)
                {
                    _diskJogForward = value;
                    if (value) _diskJogReverse = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DiskJogReverse));
                }
            }
        }

        public bool DiskJogReverse
        {
            get => _diskJogReverse;
            set
            {
                if (_diskJogReverse != value)
                {
                    _diskJogReverse = value;
                    if (value) _diskJogForward = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DiskJogForward));
                }
            }
        }

        public bool PressJogForward
        {
            get => _pressJogForward;
            set
            {
                if (_pressJogForward != value)
                {
                    _pressJogForward = value;
                    if (value) _pressJogReverse = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PressJogReverse));
                }
            }
        }

        public bool PressJogReverse
        {
            get => _pressJogReverse;
            set
            {
                if (_pressJogReverse != value)
                {
                    _pressJogReverse = value;
                    if (value) _pressJogForward = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PressJogForward));
                }
            }
        }

        public string AvgPoints
        {
            get => _avgPoints.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    _avgPoints = Math.Max(1, v);
                    OnPropertyChanged();
                }
            }
        }

        public string Notes
        {
            get => _notes;
            set
            {
                _notes = value ?? "";
                OnPropertyChanged();
            }
        }
        #endregion

        /// <summary>
        /// P26 阶梯表格行（1~16 段）。
        /// UI 使用 DataGrid 进行输入；上位机 Start 时按 StepCount 前 N 行下发。
        /// </summary>
        public sealed class P26StepRow : INotifyPropertyChanged, INotifyDataErrorInfo
        {
            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

            private readonly Dictionary<string, List<string>> _errors = new Dictionary<string, List<string>>();

            public bool HasErrors => _errors.Count > 0;

            public IEnumerable GetErrors(string? propertyName)
            {
                if (string.IsNullOrWhiteSpace(propertyName))
                    return _errors.SelectMany(kv => kv.Value);

                return _errors.TryGetValue(propertyName, out var list) ? list : Array.Empty<string>();
            }

            public int Index { get; }

            // 前 N 行可编辑，其余行置灰并禁用（由 ViewModel 的 P26StepCount 驱动刷新）
            private bool _isActive = true;
            public bool IsActive
            {
                get => _isActive;
                set
                {
                    if (_isActive == value) return;
                    _isActive = value;
                    if (!_isActive) ClearAllErrors();
                    OnPropertyChanged(nameof(IsActive));
                }
            }

            private double _ballSpeed;
            public double BallSpeed
            {
                get => _ballSpeed;
                set
                {
                    _ballSpeed = value;
                    ValidateFinite(nameof(BallSpeed), _ballSpeed);
                    OnPropertyChanged(nameof(BallSpeed));
                }
            }

            private double _diskSpeed;
            public double DiskSpeed
            {
                get => _diskSpeed;
                set
                {
                    _diskSpeed = value;
                    ValidateFinite(nameof(DiskSpeed), _diskSpeed);
                    OnPropertyChanged(nameof(DiskSpeed));
                }
            }

            private double _timeSeconds;
            public double TimeSeconds
            {
                get => _timeSeconds;
                set
                {
                    _timeSeconds = value;
                    ValidatePositive(nameof(TimeSeconds), _timeSeconds);
                    OnPropertyChanged(nameof(TimeSeconds));
                }
            }

            private double _loadN;
            public double LoadN
            {
                get => _loadN;
                set
                {
                    _loadN = value;
                    ValidateNonNegative(nameof(LoadN), _loadN);
                    OnPropertyChanged(nameof(LoadN));
                }
            }

            public P26StepRow(int index)
            {
                Index = index;
            }

            private void SetErrors(string propertyName, params string[] errors)
            {
                if (errors == null || errors.Length == 0)
                {
                    ClearErrors(propertyName);
                    return;
                }

                _errors[propertyName] = errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            }

            private void ClearErrors(string propertyName)
            {
                if (_errors.Remove(propertyName))
                    ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            }

            private void ClearAllErrors()
            {
                if (_errors.Count == 0) return;
                var keys = _errors.Keys.ToArray();
                _errors.Clear();
                foreach (var k in keys)
                    ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(k));
            }

            private void ValidateFinite(string propertyName, double v)
            {
                if (!IsActive) { ClearErrors(propertyName); return; }
                if (double.IsNaN(v) || double.IsInfinity(v))
                    SetErrors(propertyName, "必须是有限数值");
                else
                    ClearErrors(propertyName);
            }

            private void ValidatePositive(string propertyName, double v)
            {
                if (!IsActive) { ClearErrors(propertyName); return; }
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    SetErrors(propertyName, "必须是有限数值");
                    return;
                }
                if (v <= 0)
                    SetErrors(propertyName, "必须 > 0");
                else
                    ClearErrors(propertyName);
            }

            private void ValidateNonNegative(string propertyName, double v)
            {
                if (!IsActive) { ClearErrors(propertyName); return; }
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    SetErrors(propertyName, "必须是有限数值");
                    return;
                }
                if (v < 0)
                    SetErrors(propertyName, "必须 ≥ 0");
                else
                    ClearErrors(propertyName);
            }
        }

        #region ========== 实时值字段区（显示区绑定） ==========
        /// <summary>
        /// 实时值（曲线与数值显示共用）
        /// - UseSimulatedSource=true 时由 UiTick 产生模拟波形
        /// - UseSimulatedSource=false 时由 PLC 读回值/DAQ 采集值刷新
        /// </summary>
        private double _mu = double.NaN;
        private double _loadN = double.NaN;
        private double _ballSpeed = double.NaN;
        private double _diskSpeed = double.NaN;
        private double _rollingSpeed = double.NaN;
        private double _srr = double.NaN;
        private double _temperatureC = double.NaN;

        private static string FormatDouble(double v, string format)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "--";
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        // 牵引力系数（界面显示保留 5 位小数）
        public string MuText => FormatDouble(_mu, "F5");
        public string TractionForceText => FormatDouble(_tractionForceN, "F1");
        public string LoadText => FormatDouble(_loadN, "F1");
        public string BallSpeedText => FormatDouble(_ballSpeed, "F1");
        public string DiskSpeedText => FormatDouble(_diskSpeed, "F1");
        public string RollingSpeedText => FormatDouble(_rollingSpeed, "F1");
        public string SrrText => FormatDouble(_srr, "F3");
        public string TemperatureText => FormatDouble(_temperatureC, "F1");
        #endregion

        #region ========== UI 刷新参数区（UI 定时器/曲线点数/模拟源开关） ==========（UI 定时器/曲线点数/模拟源开关） ==========
        private int _uiHz = 20;

        // 显示窗口时长（s）：
        // 1) 决定时间序列曲线 X 轴自动滚动窗口宽度；
        // 2) 决定绘图缓存点数上限：MaxPoints ≈ UiHz × WindowSeconds
        //    （仅影响曲线/窗口内缓存，不影响全量记录与导出）。
        private double _plotWindowSeconds = 120.0;

        // 绘图缓存点数上限（由 UiHz 与 PlotWindowSeconds 自动计算）
        private int _maxPlotPoints = 2400;

        // —— 全量记录：边采集边落盘（不受绘图缓存上限影响）——
        private Channel<SampleSnapshot>? _recordChannel;
        private System.Threading.CancellationTokenSource? _recordCts;
        private Task? _recordTask;
        private string? _recordingPath;
        private long _recordTotalCount = 0; // Interlocked
        private int _recordFlushRequest = 0;

        private void RecomputePlotPointLimit()
        {
            int hz = Math.Max(1, _uiHz);

            double win = _plotWindowSeconds;
            if (double.IsNaN(win) || double.IsInfinity(win)) win = 120.0;
            if (win < 5) win = 5;
            if (win > 3600) win = 3600;
            _plotWindowSeconds = win;

            int max = (int)Math.Ceiling(win * hz) + 2;
            if (max < 200) max = 200;
            if (max > 200000) max = 200000;
            _maxPlotPoints = max;
        }

        /// <summary>
        /// 关键开关：是否使用模拟数据源
        /// - 联调 PLC/DAQ 时应关闭（false），否则可能会覆盖真实数据
        /// </summary>
        private bool _useSimulatedSource = false;

        public string UiHz
        {
            get => _uiHz.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, out var v) && v >= 1 && v <= 100)
                {
                    _uiHz = v;
                    _uiTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _uiHz);
                    RecomputePlotPointLimit();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SampleCountText));
                }
            }
        }

        /// <summary>
        /// 显示窗口时长（秒）：仅影响曲线显示窗口与绘图缓存点数上限，不影响全量记录与导出。
        /// </summary>
        public string PlotWindowSeconds
        {
            get => _plotWindowSeconds.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    _plotWindowSeconds = v;
                    RecomputePlotPointLimit();

                    // 若用户在运行中调窗口，立即应用到当前视图
                    ResetPlotScroll();

                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SampleCountText));
                }
            }
        }

        public string RecordingFileBriefText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_recordingPath))
                    return "记录文件: (未开始)";
                return "记录文件: " + Path.GetFileName(_recordingPath);
            }
        }

        /// <summary>
        /// t-牵引力系数曲线优化参数：EMA 时间常数 τ (s)。
        /// 仅影响 PlotMu 曲线，不影响右上角实时 μ（raw）。
        /// </summary>
        public string MuPlotTauSec
        {
            get => _muPlotEmaTauSec.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    // 允许 0（等价于不平滑），上限给一个工程合理值
                    if (v < 0) v = 0;
                    if (v > 20) v = 20;
                    _muPlotEmaTauSec = v;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// t-牵引力系数曲线优化参数：中值滤波窗口点数。
        /// 仅影响 PlotMu 曲线。
        /// </summary>
        public string MuPlotMedianWindow
        {
            get => _muPlotMedianWindow.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    if (n < 1) n = 1;
                    if (n > 101) n = 101;

                    if (n != _muPlotMedianWindow)
                    {
                        _muPlotMedianWindow = n;
                        TrimMuMedianBufferToWindow();
                        OnPropertyChanged();
                    }
                }
            }
        }

        public bool UseSimulatedSource
        {
            get => _useSimulatedSource;
            set { _useSimulatedSource = value; OnPropertyChanged(); }
        }
        #endregion

        #region ========== 曲线模型区（OxyPlot） ==========
        public PlotModel PlotMu { get; }
        public PlotModel PlotLoad { get; }
        public PlotModel PlotRollingSpeedTime { get; }
        public PlotModel PlotTemperatureTime { get; }
        public PlotModel PlotRollingSpeedMu { get; }
        public PlotModel PlotSrrMu { get; }

        private readonly LineSeries _muSeries;                 // 有效段：绿线
        private readonly LineSeries _muSeriesInvalid;          // 无效段：红线
        private readonly ScatterSeries _muInvalidEdgeMarkers;  // 无效段起止红点
        private readonly LineSeries _loadSeries;
        private readonly LineSeries _rollingSpeedSeries;
        private readonly LineSeries _rollingSpeedSeriesLog; // 对数轴专用：|v| 幅值曲线
        private readonly LineSeries _tempSeries;
        private readonly ScatterSeries _rollingMuSeries;
        private readonly ScatterSeries _srrMuSeries;

        // —— t-μ 曲线优化（仅影响 PlotMu，右上角实时 μ 仍保持 raw）——
        private double _muPlotEmaTauSec = 0.6;   // EMA 时间常数 τ (s)
        private int _muPlotMedianWindow = 10;    // 中值窗口点数（>=1）
        private readonly Queue<double> _muPlotMedianBuf = new();
        private bool _muPlotEmaInited = false;
        private double _muPlotEma = double.NaN;
        private double _muPlotLastValid = double.NaN;
        private bool _muPlotHasValid = false;
        private double _muPlotLastT = double.NaN;

        // t-μ 无效段边界标记状态机
        private bool _muInInvalid = false;
        private bool _muHasLastInvalidPoint = false;
        private DataPoint _muLastInvalidPoint = DataPoint.Undefined;

        // —— t-卷吸速度 曲线平滑（仅影响 PlotRollingSpeedTime，右上角实时仍为 raw）——
        private double _rollingSpeedPlotEmaTauSec = 0.001; // EMA 时间常数 τ (s)
        private bool _rollingSpeedPlotEmaInited = false;
        private double _rollingSpeedPlotEma = double.NaN;
        private double _rollingSpeedPlotLastT = double.NaN;

        // —— 时间轴自动滚动（用户缩放/拖动后会暂停，点击“复位滚动”恢复）——
        private bool _plotAutoScrollEnabled = true;
        private bool _suppressAxisChanged = false;


        // —— 导出用历史缓存（以 UI Tick 节拍采样，确保各通道时间戳一致） ——
        private sealed class SampleSnapshot
        {
            public double TimeSeconds { get; set; }
            public double BallSpeedMmps { get; set; }
            public double DiskSpeedMmps { get; set; }
            public double RollingSpeedMmps { get; set; }
            public double Srr { get; set; }
            public double SerialLoadRaw { get; set; }
            public double LoadN { get; set; }
            public double TractionForceN { get; set; }
            public double Mu { get; set; }
            public double TemperatureC { get; set; }
        }

        private readonly System.Collections.Generic.List<SampleSnapshot> _history = new();


// —— Auto2 导出缓存（仅自动程序2使用）——
private sealed class Auto2RawSample
{
    public double TimeSeconds { get; set; }
    public int PointIndex { get; set; }
    public double SerialLoadRaw { get; set; }
    public double LoadN { get; set; }
    public double TractionForceN { get; set; }
    public double Mu { get; set; }
    public double BallSpeedMmps { get; set; }
    public double DiskSpeedMmps { get; set; }
    public double RollingSpeedMmps { get; set; }
    public double Srr { get; set; }
    public double TemperatureC { get; set; }
}

private sealed class Auto2PointSummary
{
    public int PointIndex { get; set; }
    public double TargetDiskMmps { get; set; }
    public double TargetBallMmps { get; set; }
    public double TargetSrr { get; set; }
    public double StageTimeSeconds { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double EffectiveSeconds { get; set; }
    public double AvgTractionForceN { get; set; }
    public double AvgMu { get; set; }
    public double AvgLoadN { get; set; }
    public double AvgBallSpeedMmps { get; set; }
    public double AvgDiskSpeedMmps { get; set; }
}

private readonly System.Collections.Generic.List<Auto2RawSample> _auto2Raw = new();
private readonly System.Collections.Generic.List<Auto2PointSummary> _auto2Summary = new();


        #endregion

        #region ========== 定时器/计时区（UI Tick） ==========
        private readonly DispatcherTimer _uiTimer;
        private readonly Stopwatch _sw = new();


        // Plot 重绘节流：OxyPlot 同时重绘多张图在高频下会占用 UI 线程，导致“卡/点击无响应”。
        // 这里将重绘频率限制为 ~10Hz；数据仍可按 _uiHz 采集与缓存。
        private double _lastPlotInvalidateSeconds = 0.0;
        private const double PlotInvalidatePeriodSeconds = 0.10;
        private string _elapsedText = "00:00:00.0";
        public string ElapsedText => _elapsedText;

        public string SampleCountText
        {
            get
            {
                var total = System.Threading.Interlocked.Read(ref _recordTotalCount);
                return $"显示窗口: {_plotWindowSeconds:F0}s（≈{_maxPlotPoints}点@{_uiHz}Hz）  窗口内: {_history.Count}点  累计记录: {total}点";
            }
        }
        #endregion

        #region ========== 命令字段区（ICommand，按钮绑定的核心） ==========
        // —— 连接与基础控制 ——
        public ICommand ConnectCmd { get; }
        public ICommand DisconnectCmd { get; }

        // —— 采集预览控制（独立于试验运行，可用于联调传感器） ——
        public ICommand StartAcqCmd { get; }
        public ICommand StopAcqCmd { get; }

        // —— 工艺控制（对 PLC 输出命令的入口） ——
        public ICommand LoadCmd { get; }
        public ICommand UnloadCmd { get; }
        public ICommand StartCmd { get; }
        public ICommand StopCmd { get; }
        public ICommand EStopCmd { get; }
        public ICommand ResetAlarmCmd { get; }

        // —— 数据导出 ——
        public ICommand ExportCsvCmd { get; }
        public ICommand ExportAuto2Cmd { get; }

        // —— 坐标轴线性/对数切换（项目书要求项） ——
        public ICommand MuAxisLinearCmd { get; }
        public ICommand MuAxisLogCmd { get; }
        public ICommand LoadAxisLinearCmd { get; }
        public ICommand LoadAxisLogCmd { get; }

        // —— 曲线区：复位滚动（恢复 120s 时间窗自动滚动）——
        public ICommand ResetPlotScrollCmd { get; }

        // —— 模式切换（手动/自动） ——
        public ICommand SetModeP22Cmd { get; }
        public ICommand SetModeP24Cmd { get; }
        public ICommand SetModeJogCmd { get; }
        public ICommand SetModeLoadSystemCmd { get; }
        public ICommand PressLoadCmd { get; }
        public ICommand PressUnloadCmd { get; }
        public ICommand PressResetCmd { get; }
        public ICommand SetModeP26Cmd { get; }
        public ICommand SetModeAuto1Cmd { get; }
        public ICommand SetModeAuto2Cmd { get; }

        // —— 阶梯模式（P26）快捷输入 ——
        public ICommand P26CopyFirstRowCmd { get; }
        public ICommand P26LinearGenerateCmd { get; }
        public ICommand P26PasteCmd { get; }
        public ICommand P26ClearTailCmd { get; }

        // —— 相机（USB/网口，经 eBUS SDK） ——
        public ICommand CameraConnectCmd { get; }
        public ICommand CameraDisconnectCmd { get; }
        public ICommand CameraCaptureCmd { get; }
        public ICommand OpenPhotoFolderCmd { get; }
        #endregion

        #region ========== 构造函数（初始化绑定/定时器/点表加载） ==========
        public MainViewModel()
        {
            // ----------- 命令绑定：CanExecute 决定按钮是否可点（关键） -----------
            ConnectCmd = new RelayCommand(_ => Connect(),
                _ => !_plcConnected && _runState != RunState.Running);

            DisconnectCmd = new RelayCommand(_ => Disconnect(),
                _ => _plcConnected && _runState != RunState.Running);

            StartAcqCmd = new RelayCommand(_ => StartAcquisition(),
                _ => _plcConnected && _runState != RunState.Running && !_isAcquiring);

            StopAcqCmd = new RelayCommand(_ => StopAcquisition(),
                _ => _plcConnected && _runState != RunState.Running && _isAcquiring);

            LoadCmd = new RelayCommand(_ => Load(),
                _ => _plcConnected && _runState == RunState.Connected);

            UnloadCmd = new RelayCommand(_ => Unload(),
                _ => _plcConnected && (_runState == RunState.Loading || _runState == RunState.Running || _runState == RunState.Connected));

            StartCmd = new RelayCommand(_ => Start(),
                _ => CanStartCurrentMode());

            StopCmd = new RelayCommand(_ => Stop(),
                _ => _plcConnected && _runState == RunState.Running);

            EStopCmd = new RelayCommand(_ => EStop(),
                _ => _runState != RunState.EStop);

            ResetAlarmCmd = new RelayCommand(_ => ResetAlarm(),
                _ => _plcConnected && _runState != RunState.Running);

            ExportCsvCmd = new RelayCommand(_ => ExportCsv(),
                _ => true);

            ExportAuto2Cmd = new RelayCommand(_ => ExportAuto2Csv(), _ => true);

            // ----------- 坐标轴切换命令（线性/对数） -----------
            MuAxisLinearCmd = new RelayCommand(_ => SetYAxisScale(PlotMu, isLog: false, yTitle: "牵引力系数"));
            MuAxisLogCmd = new RelayCommand(_ => SetYAxisScale(PlotMu, isLog: true, yTitle: "牵引力系数"));

            LoadAxisLinearCmd = new RelayCommand(_ => SetYAxisScale(PlotLoad, isLog: false, yTitle: "Load (N)"));
            LoadAxisLogCmd = new RelayCommand(_ => SetYAxisScale(PlotLoad, isLog: true, yTitle: "Load (N)"));

            ResetPlotScrollCmd = new RelayCommand(_ => ResetPlotScroll());

            // ----------- 模式切换命令（当前仅 UI 文本） -----------
            SetModeP22Cmd = new RelayCommand(_ => SetMode(TrialMode.ManualP22Constant));
            SetModeP24Cmd = new RelayCommand(_ => SetMode(TrialMode.ManualP24RecipIntermittent));
            SetModeJogCmd = new RelayCommand(_ => SetMode(TrialMode.Jog));
            SetModeLoadSystemCmd = new RelayCommand(_ => SetMode(TrialMode.LoadSystem));
            PressLoadCmd = new RelayCommand(_ => PressLoadClick(), _ => CanPressOperation());
            PressUnloadCmd = new RelayCommand(_ => PressUnloadClick(), _ => CanPressOperation());
            PressResetCmd = new RelayCommand(_ => PressResetClick(), _ => CanPressReset());
            SetModeP26Cmd = new RelayCommand(_ => SetMode(TrialMode.ManualP26Step));
            SetModeAuto1Cmd = new RelayCommand(_ => SetMode(TrialMode.AutoProgram1));
            SetModeAuto2Cmd = new RelayCommand(_ => SetMode(TrialMode.AutoProgram2));

            // ----------- P26 阶梯快捷输入命令 -----------
            P26CopyFirstRowCmd = new RelayCommand(_ => P26CopyFirstRowToN());
            P26LinearGenerateCmd = new RelayCommand(_ => P26LinearGenerate());
            P26PasteCmd = new RelayCommand(_ => P26PasteFromClipboard());
            P26ClearTailCmd = new RelayCommand(_ => P26ClearTail());

            // ----------- 相机命令 -----------
            CameraConnectCmd = new RelayCommand(_ => CameraConnect(), _ => true);
            CameraDisconnectCmd = new RelayCommand(_ => CameraDisconnect(), _ => _cameraService.IsConnected);
            CameraCaptureCmd = new RelayCommand(_ => CameraCapture(isAuto: false), _ => _cameraService.IsConnected);
            OpenPhotoFolderCmd = new RelayCommand(_ => OpenPhotoFolder(), _ => true);

            // ----------- P26 阶梯表格初始化（1~16 行） -----------
            for (int i = 1; i <= 16; i++)
            {
                var row = new P26StepRow(i)
                {
                    BallSpeed = _ballSpeedSet,
                    DiskSpeed = _diskSpeedSet,
                    TimeSeconds = _workTimeSet,
                    LoadN = _loadNSet
                };
                P26Steps.Add(row);
            }

            // 初始置灰/禁用：仅前 N 行（P26StepCount）可编辑
            UpdateP26ActiveFlags();

            // ----------- 曲线初始化 -----------
            (PlotMu, _muSeries) = CreateTimePlot("t - 牵引力系数", "t (s)", "牵引力系数");
            // t-μ：有效段绿线 + 无效段红线 + 无效段起止红点（无效段曲线保持上一次有效值）
            _muSeries.Color = OxyColors.Green;
            _muSeries.StrokeThickness = 1.5;

            _muSeriesInvalid = new LineSeries
            {
                Color = OxyColors.Red,
                StrokeThickness = 1.5,
                LineStyle = LineStyle.Solid
            };
            // CreateTimePlot 已把 _muSeries 加入 PlotMu.Series，这里将“无效段线”插入到最底层
            PlotMu.Series.Insert(0, _muSeriesInvalid);

            _muInvalidEdgeMarkers = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 3.0,
                MarkerStrokeThickness = 0,
                MarkerFill = OxyColors.Red
            };
            PlotMu.Series.Add(_muInvalidEdgeMarkers);
            (PlotLoad, _loadSeries) = CreateTimePlot("t - Load", "t (s)", "Load (N)");
            (PlotRollingSpeedTime, _rollingSpeedSeries) = CreateTimePlot("t - 卷吸速度", "t (s)", "卷吸速度 (mm/s)");
            // 对数轴显示专用：卷吸速度可能为 0/负（方向/噪声/停机抖动），LogAxis 不支持 <=0。
            // 这里新增一条“幅值曲线”序列：y = max(eps, |v|)。切换为对数轴时显示该序列，线性轴时隐藏。
            _rollingSpeedSeriesLog = new LineSeries
            {
                Color = _rollingSpeedSeries.Color,
                StrokeThickness = _rollingSpeedSeries.StrokeThickness,
                LineStyle = _rollingSpeedSeries.LineStyle,
                IsVisible = false
            };
            PlotRollingSpeedTime.Series.Add(_rollingSpeedSeriesLog);
            (PlotTemperatureTime, _tempSeries) = CreateTimePlot("t - Temperature", "t (s)", "Temperature (°C)");
            (PlotRollingSpeedMu, _rollingMuSeries) = CreateScatterPlot("卷吸速度 - 牵引力系数", "卷吸速度 (mm/s)", "牵引力系数");
            (PlotSrrMu, _srrMuSeries) = CreateScatterPlot("SRR - 牵引力系数", "SRR", "牵引力系数");

            // —— 监听用户缩放/拖动：一旦手动操作坐标轴，就暂停自动滚动 ——
            AttachAutoScrollAxisHooks(PlotMu);
            AttachAutoScrollAxisHooks(PlotLoad);
            AttachAutoScrollAxisHooks(PlotRollingSpeedTime);
            AttachAutoScrollAxisHooks(PlotTemperatureTime);


            // ----------- UI 定时器初始化（刷新显示与曲线） -----------
            _uiTimer = new DispatcherTimer(DispatcherPriority.Background);
            _uiTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _uiHz);
            RecomputePlotPointLimit();
            _uiTimer.Tick += (_, __) => UiTick();

            // ----------- 初始状态 -----------
            SetRunState(RunState.Idle);
            _localAlarmText = "Alarm: None";
            _plcAlarmSummary = "";
            _alarmDetailText = "";
            NotifyStatus();

            // ----------- 加载用户配置（Offset/Scale 等持久化参数） -----------
            LoadUserSettingsOnStartup();

            // ----------- 启动计时与 UI 刷新 -----------
            _sw.Start();
            _uiTimer.Start();

            // ----------- 加载点表（关键：Key->地址映射） -----------
            LoadPointMapOnStartup();

            // ----------- 加载报警码字典（来自最新交互表） -----------
            LoadAlarmMapsOnStartup();

            // ----------- 相机帧事件（用于预览） -----------
            _cameraService.FrameArrived += OnCameraFrameArrived;
        }
        #endregion

        #region ========== 用户配置持久化（Offset/Scale 等） ==========
        private sealed class UserSettings
        {
            public double SerialLoadOffset { get; set; }
            public double SerialLoadScale { get; set; }
        }

        private string GetUserSettingsPath()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "config");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, UserSettingsFileName);
        }

        private void LoadUserSettingsOnStartup()
        {
            try
            {
                var path = GetUserSettingsPath();
                if (!File.Exists(path)) return;

                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;

                var s = JsonSerializer.Deserialize<UserSettings>(json);
                if (s == null) return;

                if (!double.IsNaN(s.SerialLoadOffset) && !double.IsInfinity(s.SerialLoadOffset))
                    _serialLoadOffset = s.SerialLoadOffset;
                if (!double.IsNaN(s.SerialLoadScale) && !double.IsInfinity(s.SerialLoadScale))
                    _serialLoadScale = s.SerialLoadScale;

                OnPropertyChanged(nameof(SerialLoadOffset));
                OnPropertyChanged(nameof(SerialLoadScale));
            }
            catch
            {
                // ignore
            }
        }

        private void RequestSaveUserSettings()
        {
            try
            {
                if (_userSettingsSaveTimer == null)
                {
                    _userSettingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(400)
                    };
                    _userSettingsSaveTimer.Tick += (_, __) =>
                    {
                        _userSettingsSaveTimer!.Stop();
                        SaveUserSettingsNow();
                    };
                }

                _userSettingsSaveTimer.Stop();
                _userSettingsSaveTimer.Start();
            }
            catch
            {
                // ignore
            }
        }

        private void SaveUserSettingsNow()
        {
            try
            {
                var path = GetUserSettingsPath();
                var s = new UserSettings
                {
                    SerialLoadOffset = _serialLoadOffset,
                    SerialLoadScale = _serialLoadScale,
                };
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }
        #endregion

        #region ========== DAQ 启停区（PCIe-1805 Buffered AI，单通道载荷） ==========
        private void StartDaqStreamingIfPossible()
        {
            if (!_plcConnected) return;
            if (_daqService.IsRunning) return;
            if (!_daqService.IsInitialized) return;
            if (UseSimulatedSource) return;

            var cfg = new DaqConfig
            {
                // 固化（不在 UI 暴露）：DeviceId=1，标定系数=4.9183(N/V)，偏置=0
                DeviceId = FixedDaqDeviceId,

                ChannelStart = 0,
                ChannelCount = 1,

                // 允许用户修改：采样率与 N 点平均
                SamplesPerSecond = _samplesPerSecond,
                AvgWindow = _avgPoints,

                // DataReady 粒度：默认 ~0.2s 数据（在 Normalize() 中自动推导）
                SectionPerChannel = 0,

                // 0 => continuous（不同 DAQNavi 版本行为略有差异，但一般可用）
                SamplesPerChannel = 0,

                ScaleFactors = new[] { FixedLoadScaleNPerV },
                Offsets = new[] { FixedLoadOffsetN },
            };

            cfg.Normalize();


            _daqStartBaseSeconds = _sw.Elapsed.TotalSeconds + TimeEpsSeconds;
            _lastDaqPointTimeSeconds = -1;
            _daqService.Start(cfg);
            _daqEnabled = _daqService.IsRunning;

            OnPropertyChanged(nameof(DaqStatusText));
            OnPropertyChanged(nameof(SerialStatusText));
        }

        private void StopDaqStreaming()
        {
            try { _daqService.Stop(); } catch { /* ignore */ }
            _daqEnabled = false;
            OnPropertyChanged(nameof(DaqStatusText));
            OnPropertyChanged(nameof(SerialStatusText));
        }


        /// <summary>
        /// 采集预览：在“未运行试验”的情况下启动 DAQ/串口采集，用于联调与实时显示。
        /// </summary>
        private void StartAcquisition()
        {
            if (!_plcConnected)
            {
                SetAlarm("Alarm: PLC not connected");
                return;
            }

            // 采集开始：重置时间轴与曲线，仅从“采集开始”产生数据点
            _isAcquiring = true;
            ResetTimeAndPlots();
            BlankRealtimeValues();
            RefreshCommandState();
            try
            {
                if (!_daqService.IsInitialized)
                    _daqService.Initialize(FixedDaqDeviceId);

                if (!_daqService.IsRunning)
                    StartDaqStreamingIfPossible();
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: DAQ Start Failed - " + ex.Message);
            }
            try
            {
                if (!_strainService.IsRunning)
                    _strainService.Start(SerialPortName, FixedSerialBaudRate, FixedSerialSlaveId, FixedSerialPollMs);
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Serial Start Failed - " + ex.Message);
            }

            NotifyStatus();
            RefreshCommandState();
        }

        /// <summary>
        /// 采集停止：停止 DAQ 与串口采集（不影响 PLC 连接）。
        /// </summary>
        private void StopAcquisition()
        {
            StopDaqStreaming();
            StopContinuousRecording();
            try { _strainService.Stop(); } catch { /* ignore */ }

            _isAcquiring = false;
            BlankRealtimeValues();

            NotifyStatus();
            RefreshCommandState();
        }

        private void ResetTimeAndPlots()
        {
            // 采集开始时：从 t=0 重新计时，并清空曲线与导出缓存
            _sw.Restart();

            _history.Clear();
            StartContinuousRecording();

            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(SampleCountText));

            _muSeries.Points.Clear();
            _muSeriesInvalid.Points.Clear();
            _muInvalidEdgeMarkers.Points.Clear();
            _muInInvalid = false;
            _muHasLastInvalidPoint = false;
            _loadSeries.Points.Clear();
            _rollingSpeedSeries.Points.Clear();
            _rollingSpeedSeriesLog.Points.Clear();
            _tempSeries.Points.Clear();
            _rollingMuSeries.Points.Clear();
            _srrMuSeries.Points.Clear();

            ResetMuPlotFilterState();

            ResetRollingSpeedPlotFilterState();
            _plotAutoScrollEnabled = true;

            PlotMu.InvalidatePlot(true);
            PlotLoad.InvalidatePlot(true);
            PlotRollingSpeedTime.InvalidatePlot(true);
            PlotTemperatureTime.InvalidatePlot(true);
            PlotRollingSpeedMu.InvalidatePlot(true);
            PlotSrrMu.InvalidatePlot(true);

            _daqStartBaseSeconds = 0.0;
            _lastDaqPointTimeSeconds = -1.0;

            // 采集开始时恢复自动滚动视图（窗口 PlotWindowSeconds）
            ResetPlotScroll();
        }



        private void StartContinuousRecording()
        {
            try
            {
                StopContinuousRecording();

                var dir = Path.Combine(AppContext.BaseDirectory, "TribologyExports");
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, $"record_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _recordingPath = path;

                System.Threading.Interlocked.Exchange(ref _recordTotalCount, 0);
                System.Threading.Interlocked.Exchange(ref _recordFlushRequest, 0);

                var ch = Channel.CreateUnbounded<SampleSnapshot>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                _recordChannel = ch;

                var cts = new System.Threading.CancellationTokenSource();
                _recordCts = cts;
                var token = cts.Token;

                _recordTask = Task.Run(async () =>
                {
                    try
                    {
                        const string header = "t_s,traction_force_N,traction_coeff,load_raw,load_N,ball_mmps,disk_mmps,reel_mmps,srr,temperature_C";

                        const string fmtT = "F4";
                        const string fmtForce = "F5";
                        const string fmtMu = "F6";
                        const string fmtLoadRaw = "F3";
                        const string fmtLoadN = "F3";
                        const string fmtSpeed = "F2";
                        const string fmtSrr = "F6";
                        const string fmtTemp = "F2";

                        static string F(double v, string fmt)
                            => (double.IsNaN(v) || double.IsInfinity(v)) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);

                        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                        using var sw = new StreamWriter(fs, new UTF8Encoding(true), 1 << 16);
                        await sw.WriteLineAsync(header).ConfigureAwait(false);

                        var lastFlush = Stopwatch.StartNew();

                        await foreach (var h in ch.Reader.ReadAllAsync(token).ConfigureAwait(false))
                        {                            await sw.WriteLineAsync(
                                $"{F(h.TimeSeconds, fmtT)},{F(h.TractionForceN, fmtForce)},{F(h.Mu, fmtMu)},{F(h.SerialLoadRaw, fmtLoadRaw)},{F(h.LoadN, fmtLoadN)},{F(h.BallSpeedMmps, fmtSpeed)},{F(h.DiskSpeedMmps, fmtSpeed)},{F(h.RollingSpeedMmps, fmtSpeed)},{F(h.Srr, fmtSrr)},{F(h.TemperatureC, fmtTemp)}"
                            ).ConfigureAwait(false);

                            System.Threading.Interlocked.Increment(ref _recordTotalCount);

                            if (System.Threading.Interlocked.Exchange(ref _recordFlushRequest, 0) == 1 || lastFlush.ElapsedMilliseconds >= 1000)
                            {
                                await sw.FlushAsync().ConfigureAwait(false);
                                lastFlush.Restart();
                            }
                        }

                        await sw.FlushAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                    catch
                    {
                        // ignore: recording failure should not crash UI
                    }
                }, token);

                OnPropertyChanged(nameof(RecordingFileBriefText));
                OnPropertyChanged(nameof(SampleCountText));
            }
            catch
            {
                _recordingPath = null;
                OnPropertyChanged(nameof(RecordingFileBriefText));
                OnPropertyChanged(nameof(SampleCountText));
            }
        }

        private void StopContinuousRecording()
        {
            try
            {
                var ch = _recordChannel;
                if (ch != null)
                    ch.Writer.TryComplete();
            }
            catch { /* ignore */ }

            _recordChannel = null;

            try { _recordCts?.CancelAfter(5000); } catch { /* ignore */ }
            _recordCts = null;

            OnPropertyChanged(nameof(RecordingFileBriefText));
            OnPropertyChanged(nameof(SampleCountText));
        }

        private void EnqueueRecordSample(SampleSnapshot snap)
        {
            try { _recordChannel?.Writer.TryWrite(snap); } catch { /* ignore */ }
        }
        private void BlankRealtimeValues()
        {
            // 未采集时：实时数值显示为 “--”
            _mu = double.NaN;
            _loadN = double.NaN;
            _tractionForceN = double.NaN;
            _ballSpeed = double.NaN;
            _diskSpeed = double.NaN;
            _rollingSpeed = double.NaN;
            _srr = double.NaN;
            _temperatureC = double.NaN;

            OnPropertyChanged(nameof(MuText));
            OnPropertyChanged(nameof(TractionForceText));
            OnPropertyChanged(nameof(LoadText));
            OnPropertyChanged(nameof(BallSpeedText));
            OnPropertyChanged(nameof(DiskSpeedText));
            OnPropertyChanged(nameof(RollingSpeedText));
            OnPropertyChanged(nameof(SrrText));
            OnPropertyChanged(nameof(TemperatureText));
        }

        #endregion

        #region ========== 点表加载区（Config\pointmap_mvp.csv -> PlcPointMap） ==========
        /// <summary>
        /// 点表加载：从程序输出目录下的 Config\pointmap_mvp.csv 读取点表。
        /// 关键点：
        /// - 你需要在 VS 的文件属性中设置 “复制到输出目录：始终复制”
        /// - 读取路径以 AppDomain.CurrentDomain.BaseDirectory 为基准
        /// </summary>
        private void LoadPointMapOnStartup()
        {
            try
            {
                _pointMap = PlcPointMap.LoadFromCsv(@"Config\pointmap_mvp.csv");
                _pointMapStatusText = $"Map: Loaded ({_pointMap.Tags.Count} tags)";
                Debug.WriteLine("[PointMap] " + _pointMapStatusText);
            }
            catch (Exception ex)
            {
                _pointMap = null;
                _pointMapStatusText = "Map: ERROR - " + ex.Message;
                Debug.WriteLine("[PointMap] " + _pointMapStatusText);
            }

            OnPropertyChanged(nameof(PointMapStatusText));
            OnPropertyChanged(nameof(PlcStatusText));
        }

        /// <summary>
        /// 加载报警码字典（来自电机公司“交互表-最终版.xlsx”）
        /// - PLC 自定义错误码：Config\plc_error_code_map.csv（500~）
        /// - 伺服报警码：Config\servo_alarm_map.csv（0x****）
        /// </summary>
        private void LoadAlarmMapsOnStartup()
        {
            try
            {
                _plcErrorCatalog = Lanzhou_v1._0.PLC.PlcErrorCodeCatalog.LoadFromCsv(@"Config\plc_error_code_map.csv");
                Debug.WriteLine($"[AlarmMap] PLC ErrorCode catalog loaded: {_plcErrorCatalog.Count} items");
            }
            catch (Exception ex)
            {
                _plcErrorCatalog = null;
                Debug.WriteLine("[AlarmMap] PLC ErrorCode catalog ERROR - " + ex.Message);
            }
            try
            {
                _servoAlarmCatalog = Lanzhou_v1._0.PLC.ServoAlarmCatalog.LoadFromCsv(@"Config\servo_alarm_map.csv");
                Debug.WriteLine($"[AlarmMap] Servo alarm catalog loaded: {_servoAlarmCatalog.Count} items");
            }
            catch (Exception ex)
            {
                _servoAlarmCatalog = null;
                Debug.WriteLine("[AlarmMap] Servo alarm catalog ERROR - " + ex.Message);
            }
        }
        #endregion

        #region ========== 绘图区辅助函数区（创建曲线/切换对数轴） ==========
        private static (PlotModel model, LineSeries series) CreateTimePlot(string title, string xTitle, string yTitle)
        {
            var model = new PlotModel { Title = title };

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = xTitle,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot
            });

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = yTitle,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot
            });

            var series = new LineSeries
            {
                StrokeThickness = 1.5,
                LineStyle = LineStyle.Solid
            };

            model.Series.Add(series);
            return (model, series);
        }

        private static (PlotModel model, ScatterSeries series) CreateScatterPlot(string title, string xTitle, string yTitle)
        {
            var model = new PlotModel { Title = title };

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = xTitle,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot
            });

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = yTitle,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot
            });

            var series = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 2.5,
                MarkerStrokeThickness = 0
            };

            model.Series.Add(series);
            return (model, series);
        }

        /// <summary>
        /// 切换 Y 轴线性/对数坐标（项目书要求项）
        /// </summary>
        private void SetYAxisScale(PlotModel model, bool isLog, string yTitle)
        {
            var oldY = model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Left);
            if (oldY != null)
                model.Axes.Remove(oldY);

            Axis newY;
            if (isLog)
            {
                newY = new LogarithmicAxis
                {
                    Position = AxisPosition.Left,
                    Title = yTitle,
                    Minimum = 1e-6,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot
                };
            }
            else
            {
                newY = new LinearAxis
                {
                    Position = AxisPosition.Left,
                    Title = yTitle,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot
                };
            }

            model.Axes.Add(newY);
            model.InvalidatePlot(false);
        }
        #endregion

        #region ========== UI Tick（实时刷新与曲线追加点） ==========
        /// <summary>
        /// UI Tick：以 _uiHz 更新一次（默认 20Hz）
        /// - 更新计时显示
        /// - 若 UseSimulatedSource=true：生成模拟值
        /// - 刷新数值绑定
        /// - 曲线追加点并裁剪到 MaxPlotPoints
        /// </summary>
        private bool _lastSourceWasSimulated = false;


        /// <summary>
        /// 计算卷吸速度与 SRR。
        /// - 卷吸速度 = (球速 + 盘速) / 2
        /// - SRR = (盘速 - 球速) / 卷吸速度
        /// </summary>
        private void UpdateDerivedKinematics()
        {
            _rollingSpeed = (_ballSpeed + _diskSpeed) / 2.0;

            if (!double.IsNaN(_rollingSpeed) && Math.Abs(_rollingSpeed) > 1e-9)
                _srr = (_diskSpeed - _ballSpeed) / _rollingSpeed;
            else
                _srr = double.NaN;

            OnPropertyChanged(nameof(RollingSpeedText));
            OnPropertyChanged(nameof(SrrText));
        }


        private void UiTick()
        {
            var t = _sw.Elapsed.TotalSeconds + TimeEpsSeconds;

            _elapsedText = _sw.Elapsed.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(ElapsedText));

            // 未点击“采集开始”时：不更新实时量、不追加曲线点（避免 t-卷吸速度出现 0 直线）
            if (!UseSimulatedSource && !_isAcquiring)
                return;

            if (UseSimulatedSource)
            {
                _lastSourceWasSimulated = true;

                _mu = 0.10 + 0.02 * Math.Sin(2 * Math.PI * 0.15 * t) + 0.002 * Math.Sin(2 * Math.PI * 1.7 * t);
                _loadN = _loadNSet + 2.0 * Math.Sin(2 * Math.PI * 0.05 * t);
                _ballSpeed = _ballSpeedSet + 5.0 * Math.Sin(2 * Math.PI * 0.08 * t);
                _diskSpeed = _diskSpeedSet + 2.0 * Math.Sin(2 * Math.PI * 0.06 * t);
                _temperatureC = 25.0 + 0.6 * Math.Sin(2 * Math.PI * 0.01 * t);
                _tractionForceN = _mu * _loadN;
            }
            else
            {
                // 真实采集：牵引力（PCIe-1805 AI0，高频/N点平均）、载荷（串口通道1）、温度（串口通道2）
                var ap = _daqService.GetLatestAveraged();
                if (ap.Channels.Length > 0)
                    _tractionForceN = ap.Channels[0];

                // 串口：通道1=载荷（Raw），通道2=温度
                if (_strainService.TryGetLatest(out var loadRaw, out var tempRaw))
                {
                    _serialLoadRaw = loadRaw;

                    // 载荷标定：Load(N) = (Raw + Offset) * Scale
                    _loadN = (loadRaw + _serialLoadOffset) * _serialLoadScale;

                    // 温度：按 1.0°C 单位输出；界面按 1°C 分辨率显示（xx.0）
                    _temperatureC = Math.Round(tempRaw, 0);
                }

                // 牵引力系数 = Traction / Load
                if (!double.IsNaN(_loadN) && !double.IsInfinity(_loadN) && Math.Abs(_loadN) > 1e-9 &&
                    !double.IsNaN(_tractionForceN) && !double.IsInfinity(_tractionForceN))
                {
                    _mu = _tractionForceN / _loadN;
                }
                else
                {
                    _mu = double.NaN;
                }

                // 状态栏刷新
                OnPropertyChanged(nameof(DaqStatusText));
                OnPropertyChanged(nameof(SerialStatusText));
            }

            // ----------- 派生量（卷吸速度与 SRR） -----------
            _rollingSpeed = 0.5 * (_ballSpeed + _diskSpeed);
            if (Math.Abs(_rollingSpeed) > 1e-9)
                _srr = (_diskSpeed - _ballSpeed) / _rollingSpeed;
            else
                _srr = double.NaN;

            // —— 时间序列 —— 
            AppendMuPlotPoint(t, _mu, _loadN);
            AppendPoint(_loadSeries, t, _loadN);
            var rsPlot = SmoothRollingSpeedForPlot(t, _rollingSpeed);
            AppendPoint(_rollingSpeedSeries, t, rsPlot);

            // 对数轴专用序列：绘制幅值 |v| 并钳制到 eps，避免 LogAxis 因 y<=0 丢点导致断线
            var rsLog = Math.Abs(rsPlot);
            const double eps = 1e-3;
            if (!double.IsNaN(rsLog) && !double.IsInfinity(rsLog))
                rsLog = Math.Max(eps, rsLog);
            AppendPoint(_rollingSpeedSeriesLog, t, rsLog);

            if (!double.IsNaN(_temperatureC) && !double.IsInfinity(_temperatureC))
                AppendPoint(_tempSeries, t, _temperatureC);

            // —— 滚动速度—μ / SRR—μ（若 μ 有效） ——
            if (!double.IsNaN(_mu) && !double.IsInfinity(_mu))
            {
                AppendPoint(_rollingMuSeries, _rollingSpeed, _mu);
                if (!double.IsNaN(_srr) && !double.IsInfinity(_srr))
                    AppendPoint(_srrMuSeries, _srr, _mu);
            }

            OnPropertyChanged(nameof(MuText));
            OnPropertyChanged(nameof(TractionForceText));
            OnPropertyChanged(nameof(LoadText));
            OnPropertyChanged(nameof(TractionForceText));
            OnPropertyChanged(nameof(BallSpeedText));
            OnPropertyChanged(nameof(DiskSpeedText));
            OnPropertyChanged(nameof(RollingSpeedText));
            OnPropertyChanged(nameof(SrrText));
            OnPropertyChanged(nameof(TemperatureText));

            // 记录一帧用于导出（以 UI Tick 时间戳为准）
            var snap = new SampleSnapshot
            {
                TimeSeconds = t,
                BallSpeedMmps = _ballSpeed,
                DiskSpeedMmps = _diskSpeed,
                RollingSpeedMmps = _rollingSpeed,
                Srr = _srr,
                SerialLoadRaw = _serialLoadRaw,
                LoadN = _loadN,
                TractionForceN = _tractionForceN,
                Mu = _mu,
                TemperatureC = _temperatureC,
            };
            _history.Add(snap);
            EnqueueRecordSample(snap);

            // 自动程序2：HMI 自算 avg（不回写数据给 PLC）
            UpdateAuto2OnTick(t);

            if (_history.Count > _maxPlotPoints)
            {
                int removeCount = _history.Count - _maxPlotPoints;
                _history.RemoveRange(0, removeCount);
            }

            // —— 时间轴自动滚动（PlotWindowSeconds）——
            AutoScrollTimePlots(t);

            if (t - _lastPlotInvalidateSeconds >= PlotInvalidatePeriodSeconds)
            {
                _lastPlotInvalidateSeconds = t;

                // false：仅请求重绘；Series.Points 已在上面更新，无需每次都强制更新数据
                PlotMu.InvalidatePlot(false);
                PlotLoad.InvalidatePlot(false);
                PlotRollingSpeedTime.InvalidatePlot(false);
                PlotTemperatureTime.InvalidatePlot(false);
                PlotRollingSpeedMu.InvalidatePlot(false);
                PlotSrrMu.InvalidatePlot(false);
            }

            TryWriteFeedbackToPlc(t);

            // 加载系统：双保险停机判据（载荷到设定值 / 下压限位触发）
            CheckLoadSystemStopConditions();
        }

        private void TryWriteFeedbackToPlc(double nowSeconds)
        {
            if (!_plcConnected || !_feedbackTagsReady) return;
            if (nowSeconds - _lastFeedbackWriteSeconds < FeedbackWritePeriodSeconds) return;
            _lastFeedbackWriteSeconds = nowSeconds;

            _ = WriteFeedbackToPlcAsync(_loadN, _temperatureC, _tractionForceN);
        }

        private async System.Threading.Tasks.Task WriteFeedbackToPlcAsync(double loadN, double tempC, double forceN)
        {
            if (!EnsurePlcWriteReady() || !_feedbackTagsReady) return;
            if (!await _feedbackWriteLock.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (!double.IsNaN(loadN))
                    await WriteFloat32HoldingByKeyAsync("MeasHmi_LoadN", loadN).ConfigureAwait(false);
                if (!double.IsNaN(tempC))
                    await WriteFloat32HoldingByKeyAsync("MeasHmi_TempC", tempC).ConfigureAwait(false);
                if (!double.IsNaN(forceN))
                    await WriteFloat32HoldingByKeyAsync("MeasHmi_ForceN", forceN).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                _feedbackTagsReady = false;
            }
            catch
            {
            }
            finally
            {
                _feedbackWriteLock.Release();
            }
        }

        private void AppendPoint(LineSeries series, double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                return;

            series.Points.Add(new DataPoint(x, y));

            if (series.Points.Count > _maxPlotPoints)
            {
                int removeCount = series.Points.Count - _maxPlotPoints;
                series.Points.RemoveRange(0, removeCount);
            }
        }

        /// <summary>
        /// LineSeries 追加点（允许 y=NaN 用于断开线段；禁止 Infinity）。
        /// 仅用于需要“断线”的曲线（例如 t-μ 有效/无效段分离）。
        /// </summary>
        private void AppendPointAllowNaN(LineSeries series, double x, double y)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsInfinity(y))
                return;

            series.Points.Add(new DataPoint(x, y));

            if (series.Points.Count > _maxPlotPoints)
            {
                int removeCount = series.Points.Count - _maxPlotPoints;
                series.Points.RemoveRange(0, removeCount);
            }
        }


        /// <summary>
        /// t-μ 曲线追加点：
        /// - μ_raw 用于右上角实时显示与导出（不在此处改变）
        /// - PlotMu 显示 μ_plot：中值滤波 + EMA（可在界面调整 τ 与窗口数）
        /// - 分母门限：Fn_min = max(3N, 0.05*LoadSet)
        /// - 若分母无效：曲线保持上一次有效值，并用红点标记该时刻
        /// </summary>
        private void AppendMuPlotPoint(double t, double muRaw, double loadN)
        {
            // 计算分母门限：Fn_min = max(3N, 0.05*LoadSet)
            double fnMin = 3.0;
            if (!double.IsNaN(_loadNSet) && !double.IsInfinity(_loadNSet))
                fnMin = Math.Max(fnMin, 0.05 * _loadNSet);

            bool denomOk = !double.IsNaN(loadN) && !double.IsInfinity(loadN) && Math.Abs(loadN) >= fnMin;
            bool muOk = !double.IsNaN(muRaw) && !double.IsInfinity(muRaw);
            bool isValid = denomOk && muOk;

            if (isValid)
            {
                // 中值窗口
                _muPlotMedianBuf.Enqueue(muRaw);
                TrimMuMedianBufferToWindow();
                double median = ComputeMedian(_muPlotMedianBuf);

                // EMA（基于实际 dt）
                double dt = (!double.IsNaN(_muPlotLastT) && t > _muPlotLastT) ? (t - _muPlotLastT) : (1.0 / Math.Max(1, _uiHz));
                double tau = _muPlotEmaTauSec;
                if (tau < 1e-9) tau = 0.0;
                double alpha = (tau <= 0) ? 1.0 : (dt / (tau + dt));

                if (!_muPlotEmaInited || double.IsNaN(_muPlotEma) || double.IsInfinity(_muPlotEma))
                {
                    _muPlotEma = median;
                    _muPlotEmaInited = true;
                }
                else
                {
                    _muPlotEma = _muPlotEma + alpha * (median - _muPlotEma);
                }

                _muPlotLastValid = _muPlotEma;
                _muPlotHasValid = true;
                _muPlotLastT = t;

                // 若刚从无效恢复到有效：在无效结束点打一个红点（使用无效段最后一个点）
                if (_muInInvalid && _muHasLastInvalidPoint &&
                    !double.IsNaN(_muLastInvalidPoint.Y) && !double.IsInfinity(_muLastInvalidPoint.Y))
                {
                    AppendPoint(_muInvalidEdgeMarkers, _muLastInvalidPoint.X, _muLastInvalidPoint.Y);
                }
                _muInInvalid = false;
                _muHasLastInvalidPoint = false;

                // 有效段：绿线追加；无效段线不追加（避免跨段连线）
                AppendPoint(_muSeries, t, _muPlotLastValid);
            }
            else
            {
                // 无效：保持上一次有效值（若没有有效值则不画线），并仅在无效段起止打红点
                double y = _muPlotHasValid ? _muPlotLastValid : double.NaN;
                _muPlotLastT = t;

                // 首次进入无效段：断开绿线；同时断开红线（避免连接到历史无效段），并打“开始红点”
                if (!_muInInvalid)
                {
                    AppendPointAllowNaN(_muSeries, t, double.NaN); // 断开绿线

                    if (_muSeriesInvalid.Points.Count > 0)
                        AppendPointAllowNaN(_muSeriesInvalid, t, double.NaN); // 断开无效段线（新段起点）

                    if (!double.IsNaN(y) && !double.IsInfinity(y))
                        AppendPoint(_muInvalidEdgeMarkers, t, y); // 无效开始红点

                    _muInInvalid = true;
                }

                // 无效段：红线追加保持值（若 y 无效则跳过）
                if (!double.IsNaN(y) && !double.IsInfinity(y))
                {
                    AppendPoint(_muSeriesInvalid, t, y);
                    _muLastInvalidPoint = new DataPoint(t, y);
                    _muHasLastInvalidPoint = true;
                }
            }
        }

        private void ResetMuPlotFilterState()
        {
            _muPlotMedianBuf.Clear();
            _muPlotEmaInited = false;
            _muPlotEma = double.NaN;
            _muPlotLastValid = double.NaN;
            _muPlotHasValid = false;
            _muPlotLastT = double.NaN;
        }

        private void TrimMuMedianBufferToWindow()
        {
            while (_muPlotMedianBuf.Count > _muPlotMedianWindow)
                _muPlotMedianBuf.Dequeue();
        }

        private static double ComputeMedian(IEnumerable<double> seq)
        {
            var arr = seq.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
            if (arr.Length == 0) return double.NaN;
            Array.Sort(arr);
            int mid = arr.Length / 2;
            if ((arr.Length & 1) == 1)
                return arr[mid];
            return 0.5 * (arr[mid - 1] + arr[mid]);
        }

        private void AppendPoint(ScatterSeries series, double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                return;

            series.Points.Add(new ScatterPoint(x, y));

            while (series.Points.Count > _maxPlotPoints)
                series.Points.RemoveAt(0);
        }


        /// <summary>
        /// t-卷吸速度曲线平滑（EMA）：仅影响 PlotRollingSpeedTime。
        /// </summary>
        private double SmoothRollingSpeedForPlot(double t, double x)
        {
            // 不剔除无效点：NaN/Inf 原样返回（让曲线自然断开/不绘制）
            if (double.IsNaN(x) || double.IsInfinity(x))
                return x;

            double dt = (!double.IsNaN(_rollingSpeedPlotLastT) && t > _rollingSpeedPlotLastT)
                ? (t - _rollingSpeedPlotLastT)
                : (1.0 / Math.Max(1, _uiHz));

            double tau = _rollingSpeedPlotEmaTauSec;
            if (tau < 1e-9) tau = 0.0;
            double alpha = (tau <= 0) ? 1.0 : (dt / (tau + dt));

            if (!_rollingSpeedPlotEmaInited || double.IsNaN(_rollingSpeedPlotEma) || double.IsInfinity(_rollingSpeedPlotEma))
            {
                _rollingSpeedPlotEma = x;
                _rollingSpeedPlotEmaInited = true;
            }
            else
            {
                _rollingSpeedPlotEma = _rollingSpeedPlotEma + alpha * (x - _rollingSpeedPlotEma);
            }

            _rollingSpeedPlotLastT = t;

            var y = _rollingSpeedPlotEma;

            return y;
        }

        private void ResetRollingSpeedPlotFilterState()
        {
            _rollingSpeedPlotEmaInited = false;
            _rollingSpeedPlotEma = double.NaN;
            _rollingSpeedPlotLastT = double.NaN;
        }

        private static bool TryGetLogYAxisMinimum(PlotModel model, out double min)
        {
            min = 0.0;
            var yAxis = model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Left);
            if (yAxis is LogarithmicAxis log)
            {
                min = log.Minimum;
                return true;
            }
            return false;
        }

        private void AttachAutoScrollAxisHooks(PlotModel model)
        {
            var x = model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (x == null) return;
            x.AxisChanged += (_, __) =>
            {
                // 用户缩放/拖动后，OxyPlot 会触发 AxisChanged；此时暂停自动滚动
                if (!_suppressAxisChanged)
                    _plotAutoScrollEnabled = false;
            };
        }

        private void AutoScrollTimePlots(double t)
        {
            if (!_plotAutoScrollEnabled) return;

            _suppressAxisChanged = true;
            try
            {
                ApplyTimeWindow(PlotMu, t);
                ApplyTimeWindow(PlotLoad, t);
                ApplyTimeWindow(PlotRollingSpeedTime, t);
                ApplyTimeWindow(PlotTemperatureTime, t);
            }
            finally
            {
                _suppressAxisChanged = false;
            }
        }

        private void ApplyTimeWindow(PlotModel model, double t)
        {
            var x = model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (x == null) return;

            double max = Math.Max(0.0, t);

            double min = Math.Max(0.0, max - _plotWindowSeconds);
            // 避免 min==max 导致 Zoom 异常
            if (max - min < 1e-6) max = min + 1e-6;

            // 通过 Zoom 设置视窗范围；每个 Tick 维护一次即可实现“滚动时间窗”
            x.Zoom(min, max);
        }

        private void ResetPlotScroll()
        {
            _plotAutoScrollEnabled = true;

            _suppressAxisChanged = true;
            try
            {
                // 恢复各曲线的坐标轴视图
                foreach (var m in new[] { PlotMu, PlotLoad, PlotRollingSpeedTime, PlotTemperatureTime })
                {
                    foreach (var a in m.Axes)
                        a.Reset();
                }

                // 立即应用一次时间窗（PlotWindowSeconds）
                var t = _sw.Elapsed.TotalSeconds + TimeEpsSeconds;
                AutoScrollTimePlots(t);
            }
            finally
            {
                _suppressAxisChanged = false;
            }

            PlotMu.InvalidatePlot(false);
            PlotLoad.InvalidatePlot(false);
            PlotRollingSpeedTime.InvalidatePlot(false);
            PlotTemperatureTime.InvalidatePlot(false);
        }

        /// <summary>
        /// 供界面在切换 PlotRollingSpeedTime 的 Y 轴线性/对数时调用：通过双序列切换避免对数轴断线。
        /// 线性轴：显示原序列（可正可负）；对数轴：显示幅值序列（|v|，且已钳制到 eps）。
        /// </summary>
        public void SetRollingTimeYAxisIsLog(bool isLog)
        {
            _rollingSpeedSeries.IsVisible = !isLog;
            _rollingSpeedSeriesLog.IsVisible = isLog;
            PlotRollingSpeedTime?.InvalidatePlot(true);
        }

        #endregion

        #region ========== PLC 连接/断开与轮询区（关键：联调入口） ==========
        /// <summary>
        /// Connect：建立 Modbus TCP 连接并启动轮询任务
        /// </summary>
        private async void Connect()
        {
            try
            {
                await _plcClient.ConnectAsync(PlcIp, PlcPort, PlcUnitId);

                _plcConnected = true;


                // PLC 连接仅建立通信与轮询；DAQ/串口在“采集开始”时初始化/启动

                SetRunState(RunState.Connected);
                NotifyStatus();
                RefreshCommandState();

                StartPlcPolling();
            }
            catch (Exception ex)
            {
                _plcConnected = false;
                StopDaqStreaming();

                try { _strainService.Stop(); } catch { /* ignore */ }
                SetRunState(RunState.Idle);

                _plcProbeText = "Probe: Connect Failed - " + ex.Message;

                NotifyStatus();
                RefreshCommandState();
            }
        }

        /// <summary>
        /// Disconnect：停止轮询并断开 TCP 连接
        /// </summary>
        private void Disconnect()
        {
            StopDaqStreaming();
            try { _strainService.Stop(); } catch { /* ignore */ }
            StopPlcPolling();
            StopContinuousRecording();
            StopAuto2Run("disconnect");
            _plcClient.Disconnect();

            _isAcquiring = false;
            BlankRealtimeValues();

            _plcConnected = false;
            _daqEnabled = false;

            SetRunState(RunState.Idle);

            _plcProbeText = "Probe: Disconnected";

            NotifyStatus();
            RefreshCommandState();
        }

        /// <summary>
        /// PLC 轮询：后台 Task 每 200ms 读取一次快照（DI/IR），并更新 PlcProbeText
        /// 注意：后台线程更新 UI 必须 Dispatcher.Invoke
        /// </summary>
        private void StartPlcPolling()
        {
            StopPlcPolling();

            _plcPollCts = new System.Threading.CancellationTokenSource();
            var token = _plcPollCts.Token;

            _plcProbeText = "Probe: Polling...";
            OnPropertyChanged(nameof(PlcProbeText));
            OnPropertyChanged(nameof(SampleCountText));

            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var snap = await _plcClient.ReadMvpSnapshotAsync();

                        string line =
                            $"Probe: EnDone(B/D/P)={snap.BallEnableDone}/{snap.DiskEnableDone}/{snap.PressEnableDone} | " +
                            $"Limit={snap.PressLimit} Done={snap.MotionDone} Fail={snap.MotionFail} | Auto2Single={snap.Auto2SingleDone} | PhotoReq={snap.PhotoRequestRaw} | Err={snap.ErrorCode} | " +
                            $"ServoA(B/D/P)=0x{snap.BallAlarmCode:X4}/0x{snap.DiskAlarmCode:X4}/0x{snap.PressAlarmCode:X4} | " +
                            $"vBall={snap.BallSpeed:F3} vDisk={snap.DiskSpeed:F3} (mm/s) | LoadLim={snap.LoadAlarmLimit:F1}N";

                                                if (System.Threading.Interlocked.Exchange(ref _plcUiUpdatePending, 1) == 0)
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {

                            var tNowSeconds = _sw.Elapsed.TotalSeconds;
                            _pressLimit = snap.PressLimit;
                            // 运动完成/失败：缓存 + 上升沿锁存（避免短脉冲被漏读）
                            var doneNow = snap.MotionDone;
                            var failNow = snap.MotionFail;
                            _plcMotionDone = doneNow;
                            _plcMotionFail = failNow;
                            if (doneNow && !_plcMotionDonePrev) _plcMotionDoneLatched = true;
                            if (failNow && !_plcMotionFailPrev) _plcMotionFailLatched = true;
                            _plcMotionDonePrev = doneNow;
                            _plcMotionFailPrev = failNow;


                            _plcBallEnableDone = snap.BallEnableDone;
                            _plcDiskEnableDone = snap.DiskEnableDone;
                            _plcPressEnableDone = snap.PressEnableDone;
                            var auto2SingleNow = snap.Auto2SingleDone;
                            _plcAuto2SingleDonePrev = _plcAuto2SingleDone;
                            _plcAuto2SingleDone = auto2SingleNow;

                            _plcLoadAlarmLimit = snap.LoadAlarmLimit;
                            _plcProbeText = line;
                            OnPropertyChanged(nameof(PlcProbeText));
                            OnPropertyChanged(nameof(SampleCountText));

                            // 将新增的报警码表与状态信息结合：输出摘要 + 详情（状态区）
                            UpdatePlcAlarmFromSnapshot(snap);

                            // PLC 实际反馈速度始终记录在 Raw 字段，用于 Probe 显示
                            _plcBallSpeedRaw = snap.BallSpeed;
                            _plcDiskSpeedRaw = snap.DiskSpeed;

                            if (!UseSimulatedSource)
                            {
                                // PLC→HMI：拍照请求（4x 407）
                                _plcPhotoRequestRaw = snap.PhotoRequestRaw;



var reqActiveNow = _plcPhotoRequestRaw != 0;
if (reqActiveNow && !_plcPhotoReqPrevActive)
{
    _plcPhotoReqSeenCount++;
    _plcPhotoReqLastSeenLocal = DateTime.Now;
    OnPropertyChanged(nameof(PlcPhotoReqSeenInfo));
}
_plcPhotoReqPrevActive = reqActiveNow;
OnPropertyChanged(nameof(PlcPhotoRequestRaw));
OnPropertyChanged(nameof(IsPlcPhotoRequestActive));
OnPropertyChanged(nameof(PlcPhotoRequestRawText));
                                // 曲线/数值显示：仅在“采集开始”后启用（满足：未采集时不产生 0 直线）
                                if (_isAcquiring)
                                {
                                    _ballSpeed = _plcBallSpeedRaw;
                                    _diskSpeed = _plcDiskSpeedRaw;

                                    OnPropertyChanged(nameof(BallSpeedText));
                                    OnPropertyChanged(nameof(DiskSpeedText));
                                }

                                // 自动程序1：按 PLC 的拍照请求位进行握手（请求=1 -> 拍照 -> Done=1；PLC 清零请求后 HMI 复位 Done）
                                UpdateAuto1PhotoState(tNowSeconds);
                            }
                        
                                }
                                finally
                                {
                                    System.Threading.Interlocked.Exchange(ref _plcUiUpdatePending, 0);
                                }
                            }), DispatcherPriority.Background);
                        }

                    }
                    catch (Exception ex)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            _plcProbeText = "Probe: Read Failed - " + ex.Message;
                            OnPropertyChanged(nameof(PlcProbeText));
                            OnPropertyChanged(nameof(SampleCountText));
                        });
                    }

                    try
                    {
                        int delayMs = (_mode == TrialMode.AutoProgram2 && _runState == RunState.Running) ? 20 : 50;
                        await System.Threading.Tasks.Task.Delay(delayMs, token);
                    }
                    catch { /* ignore */ }
                }
            }, token);
        }

        private void StopPlcPolling()
        {
            try { _plcPollCts?.Cancel(); } catch { /* ignore */ }
            _plcPollCts = null;
        }

        /// <summary>
        /// 轮询快照 -> 报警摘要/详情
        /// - ErrorCode：PLC 自定义错误码（MW800，500~）
        /// - *AlarmCode：伺服报警码（DWord，0x****）
        /// 结合状态位：Limit/Done/Fail
        /// </summary>
        private void UpdatePlcAlarmFromSnapshot(Lanzhou_v1._0.PLC.PlcProbeSnapshot snap)
        {
            try
            {
                int err = snap.ErrorCode;
                string errText = "";
                if (err != 0 && _plcErrorCatalog != null && _plcErrorCatalog.TryGet(err, out var t))
                    errText = t;

                // 伺服报警：按轴收集
                var servoParts = new List<(string axis, uint code, Lanzhou_v1._0.PLC.ServoAlarmInfo? info)>();

                if (snap.BallAlarmCode != 0)
                {
                    Lanzhou_v1._0.PLC.ServoAlarmInfo? info = null;
                    if (_servoAlarmCatalog != null)
                        _servoAlarmCatalog.TryGet((int)snap.BallAlarmCode, out info);
                    servoParts.Add(("球", snap.BallAlarmCode, info));
                }
                if (snap.DiskAlarmCode != 0)
                {
                    Lanzhou_v1._0.PLC.ServoAlarmInfo? info = null;
                    if (_servoAlarmCatalog != null)
                        _servoAlarmCatalog.TryGet((int)snap.DiskAlarmCode, out info);
                    servoParts.Add(("盘", snap.DiskAlarmCode, info));
                }
                if (snap.PressAlarmCode != 0)
                {
                    Lanzhou_v1._0.PLC.ServoAlarmInfo? info = null;
                    if (_servoAlarmCatalog != null)
                        _servoAlarmCatalog.TryGet((int)snap.PressAlarmCode, out info);
                    servoParts.Add(("下压", snap.PressAlarmCode, info));
                }

                bool hasErr = err != 0;
                bool hasServo = servoParts.Count > 0;
                bool hasFail = snap.MotionFail;
                bool hasLimit = snap.PressLimit;

                // 没有任何异常：清空 PLC 报警展示（不影响本地报警）
                if (!hasErr && !hasServo && !hasFail && !hasLimit)
                {
                    if (!string.IsNullOrWhiteSpace(_plcAlarmSummary) || !string.IsNullOrWhiteSpace(_alarmDetailText))
                    {
                        _plcAlarmSummary = "";
                        _alarmDetailText = "";
                        OnPropertyChanged(nameof(AlarmText));
                        OnPropertyChanged(nameof(AlarmDetailText));
                    }
                    return;
                }

                // 摘要：尽量短，适合状态区一行显示
                var summaryParts = new List<string>();

                if (hasErr)
                {
                    summaryParts.Add(string.IsNullOrWhiteSpace(errText) ? $"Err={err}" : $"Err={err} {errText}");
                }

                if (hasServo)
                {
                    foreach (var p in servoParts)
                    {
                        string note = p.info?.Note ?? "未知伺服报警";
                        summaryParts.Add($"{p.axis}=0x{p.code:X4} {note}");
                    }
                }

                if (!hasErr)
                {
                    if (hasFail) summaryParts.Add("MotionFail");
                    if (hasLimit) summaryParts.Add("Limit");
                }

                _plcAlarmSummary = "Alarm: PLC " + string.Join(" | ", summaryParts);

                // 详情：多行
                var sb = new StringBuilder();
                sb.AppendLine($"PLC状态: Limit={snap.PressLimit} Done={snap.MotionDone} Fail={snap.MotionFail}");
                sb.AppendLine(string.IsNullOrWhiteSpace(errText) ? $"错误码(MW800): {err}" : $"错误码(MW800): {err} - {errText}");
                sb.AppendLine($"伺服报警(DWord): Ball=0x{snap.BallAlarmCode:X4}, Disk=0x{snap.DiskAlarmCode:X4}, Press=0x{snap.PressAlarmCode:X4}");

                foreach (var p in servoParts)
                {
                    string note = p.info?.Note ?? "未知伺服报警";
                    sb.AppendLine($"[{p.axis}] 0x{p.code:X4} - {note}");
                    if (p.info != null)
                    {
                        if (!string.IsNullOrWhiteSpace(p.info.Cause))
                            sb.AppendLine("  原因: " + p.info.Cause);
                        if (!string.IsNullOrWhiteSpace(p.info.Action))
                            sb.AppendLine("  处理: " + p.info.Action);
                    }
                }

                _alarmDetailText = sb.ToString().TrimEnd();

                OnPropertyChanged(nameof(AlarmText));
                OnPropertyChanged(nameof(AlarmDetailText));

                // 状态机：检测到 PLC/伺服报警则进入 Fault（非急停）
                if (_runState != RunState.EStop && _runState != RunState.Fault)
                {
                    if (hasErr || hasServo || hasFail || hasLimit)
                        SetRunState(RunState.Fault);
                }
            }
            catch
            {
                // 解析字典失败不影响主循环
            }
        }
        #endregion

        #region ========== PLC 写入辅助区（点表 Key 驱动，关键） ==========
        /// <summary>
        /// 写前置检查：必须 PLC 已连接、点表已加载
        /// </summary>
        private bool EnsurePlcWriteReady()
        {
            if (!_plcConnected)
            {
                SetAlarm("Alarm: PLC not connected");
                return false;
            }
            if (_pointMap == null)
            {
                SetAlarm("Alarm: PointMap not loaded");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 从点表中按 Key 取线圈点，并做类型检查（Area=Coil, ValueType=Bool）
        /// </summary>
        private PlcTag GetCoilTagOrThrow(string key)
        {
            if (_pointMap == null) throw new InvalidOperationException("PointMap not loaded");

            var tag = _pointMap.Get(key);

            if (tag.Area != PlcArea.Coil)
                throw new InvalidOperationException($"Tag '{key}' Area must be Coil, but is {tag.Area}");

            if (tag.ValueType != PlcValueType.Bool)
                throw new InvalidOperationException($"Tag '{key}' ValueType must be Bool, but is {tag.ValueType}");

            return tag;
        }

        private async Task WriteCoilByKeyAsync(string key, bool value)
        {
            var tag = GetCoilTagOrThrow(key);
            await _plcClient.WriteCoilAsync(tag.Address, value);
        }

        private async Task PulseCoilByKeyAsync(string key, int pulseMs = CommandPulseMs)
        {
            var tag = GetCoilTagOrThrow(key);
            await _plcClient.PulseCoilAsync(tag.Address, pulseMs);
        }



        /// <summary>
        /// 兼容写命令位：同时尝试按 0x(Coils/FC05) 与 4x(Holding/FC06) 写入。
        ///
        /// 背景：MotorFinalTest_V3 能驱动电机，是通过 WriteSingleRegister(FC06) 向 300/323 等地址写 0/1。
        /// 但点表里这些命令位按 Coil 定义，现场映射可能同时支持两种口径。
        ///
        /// 策略：两种写法“至少成功一种”即视为成功；两种都失败则抛出最后异常。
        /// </summary>
        private async Task WriteCommandCompatByKeyAsync(string key, bool value)
        {
            var tag = GetCoilTagOrThrow(key);
            bool ok = false;
            Exception? last = null;
            try
            {
                await _plcClient.WriteCoilAsync(tag.Address, value).ConfigureAwait(false);
                ok = true;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            try
            {
                await _plcClient.WriteUInt16HoldingRegisterAsync(tag.Address, (ushort)(value ? 1 : 0)).ConfigureAwait(false);
                ok = true;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (!ok)
                throw last ?? new Exception($"WriteCommandCompat failed for '{key}'");
        }

        /// <summary>
        /// 兼容脉冲：用于 PLC 上升沿触发类命令。
        /// </summary>
        private async Task PulseCommandCompatByKeyAsync(string key, int pulseMs = 150)
        {
            await WriteCommandCompatByKeyAsync(key, true).ConfigureAwait(false);
            await Task.Delay(pulseMs).ConfigureAwait(false);
            await WriteCommandCompatByKeyAsync(key, false).ConfigureAwait(false);
        }

        /// <summary>
        /// 对一组命令位做“同时置 1 → 保持 → 同时清 0”的兼容脉冲。
        ///
        /// 设计目的：
        /// - 避免像 P2.2/ P2.4 这类“球盘需同步启动”的模式中，按键脉冲被串行 await 导致的人为时间差。
        ///
        /// 说明：
        /// - 为保持现场兼容性，仍复用 WriteCommandCompatByKeyAsync（Coil/4x 两套口径至少成功一种）。
        /// - 由于 ModbusPlcClient 内部有 _ioLock 串行化 IO，本方法无法做到“同一帧”原子写入，
        ///   但能将球/盘置位时间差收敛到几个请求往返时间量级（通常远小于 1 s），满足同步启动需要。
        /// </summary>
        private async Task PulseCommandCompatGroupAsync(string[] keys, int pulseMs)
        {
            if (keys == null || keys.Length == 0) return;

            // 1) 同时置 1（尽可能紧凑地连续写入）
            for (int i = 0; i < keys.Length; i++)
                await WriteCommandCompatByKeyAsync(keys[i], true).ConfigureAwait(false);

            // 2) 保持
            await Task.Delay(pulseMs).ConfigureAwait(false);

            // 3) 同时清 0
            for (int i = 0; i < keys.Length; i++)
                await WriteCommandCompatByKeyAsync(keys[i], false).ConfigureAwait(false);
        }

        /// <summary>
        /// 点动模式最小闭环准备（参考 MotorFinalTest_V3）：
        /// - Mode=0
        /// - 写入点动加速度/速度
        /// </summary>
        private async Task ApplyJogSetupFromReferenceAsync()
        {
            // 点动模式码：参考 MotorFinalTest_V3（600=0）
            await WriteInt16HoldingByKeyAsync("Param_ModeSwitch", ModeCode_Jog).ConfigureAwait(false);

            await WriteFloat32HoldingByKeyAsync("Param_JogAcc", _jogAccSet).ConfigureAwait(false);
            await WriteFloat32HoldingByKeyAsync("Param_JogDec", _jogDecSet).ConfigureAwait(false);

            await WriteFloat32HoldingByKeyAsync("Param_BallJogSpeed", _ballJogSpeedSet).ConfigureAwait(false);
            await WriteFloat32HoldingByKeyAsync("Param_DiskJogSpeed", _diskJogSpeedSet).ConfigureAwait(false);
            await WriteFloat32HoldingByKeyAsync("Param_PressJogSpeed", _pressJogSpeedSet).ConfigureAwait(false);
        }
        private PlcTag GetHoldingTagOrThrow(string key, PlcValueType expectedType)
        {
            if (_pointMap == null) throw new InvalidOperationException("PointMap not loaded");

            var tag = _pointMap.Get(key);

            if (tag.Area != PlcArea.HoldingRegister)
                throw new InvalidOperationException($"Tag '{key}' Area must be HoldingRegister, but is {tag.Area}");

            if (tag.ValueType != expectedType)
                throw new InvalidOperationException($"Tag '{key}' ValueType must be {expectedType}, but is {tag.ValueType}");

            return tag;
        }

        private async Task WriteFloat32HoldingByKeyAsync(string key, double engValue)
        {
            var tag = GetHoldingTagOrThrow(key, PlcValueType.Float32);
            double raw = (engValue - tag.Offset) / (tag.Scale == 0 ? 1.0 : tag.Scale);
            await _plcClient.WriteFloat32HoldingRegistersAsync(tag.Address, (float)raw).ConfigureAwait(false);
        }

        private async Task WriteInt16HoldingByKeyAsync(string key, short value)
        {
            var tag = GetHoldingTagOrThrow(key, PlcValueType.Int16);
            // 对 Int16 不做 scale/offset 处理（按整数语义）
            await _plcClient.WriteUInt16HoldingRegisterAsync(tag.Address, unchecked((ushort)value)).ConfigureAwait(false);
        }

private async Task WriteInt32HoldingByKeyAsync(string key, int value)
{
    var tag = GetHoldingTagOrThrow(key, PlcValueType.Int32);
    // 对 Int32 不做 scale/offset 处理（按整数语义）
    await _plcClient.WriteInt32HoldingRegistersAsync(tag.Address, value).ConfigureAwait(false);
}

        #endregion

        #region ========== 按钮动作区（对外命令入口） ==========
        // 约定：模式码（来自《交互表-最终版.xlsx》“2：模式切换”说明）：
        // 0：点动模式；1：P2.2（恒速）；2：P2.4（往复/间歇）；3：P2.6（阶梯）；4：自动模式1；5：自动模式2
        private const short ModeCode_Jog = 0;
        private const short ModeCode_P22 = 1;
        private const short ModeCode_P24 = 2;
        private const short ModeCode_P26 = 3;
        private const short ModeCode_Auto1 = 4;
        private const short ModeCode_Auto2 = 5;

        /// <summary>
        /// Load：轴控使能（保持位）
        /// - 球驱动使能、盘驱动使能、下压驱动使能
        /// </summary>
        private async void Load()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                // 参考 MotorFinalTest_V3：先清零/复位，再写参数，再使能

                // 0) 清除点动保持位，避免逻辑打架
                await WriteCommandCompatByKeyAsync("Cmd_BallJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_BallJogReverseHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogReverseHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);

                // 1) 清除总停止、断开使能
                await WriteCommandCompatByKeyAsync("Cmd_TotalStop", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_BallDriveEnable", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskDriveEnable", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressDriveEnable", false).ConfigureAwait(false);

                // 2) 强力复位脉冲（MotorFinalTest_V3：复位保持约 1s）
                await PulseCommandCompatByKeyAsync("Cmd_TotalReset", 1000).ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);

                // 3) 点动模式与参数下发（解决参数乱码/AxisNotReady）
                SetMode(TrialMode.Jog);
                await ApplyJogSetupFromReferenceAsync().ConfigureAwait(false);

                // 4) 三轴同步使能（保持位）
                await WriteCommandCompatByKeyAsync("Cmd_BallDriveEnable", true).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskDriveEnable", true).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressDriveEnable", true).ConfigureAwait(false);

                // 等待使能生效
                await Task.Delay(500).ConfigureAwait(false);

                SetRunState(RunState.Loading);
                _localAlarmText = "Alarm: None";
                _alarmDetailText = "";
                NotifyStatus();
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Load Failed - " + ex.Message);
            }
        }

        private async void Unload()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                // 停止点动
                await WriteCommandCompatByKeyAsync("Cmd_BallJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_BallJogReverseHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogReverseHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);

                // 取消加载/卸载中的下压运动
                CancelPressOperationSilently();
                _pressStartHolding = false;
                await WriteCommandCompatByKeyAsync("Cmd_PressStart", false).ConfigureAwait(false);

                // 断使能
                await WriteCommandCompatByKeyAsync("Cmd_BallDriveEnable", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskDriveEnable", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressDriveEnable", false).ConfigureAwait(false);

                SetRunState(RunState.Connected);
                _localAlarmText = "Alarm: None";
                _alarmDetailText = "";
                NotifyStatus();
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Unload Failed - " + ex.Message);
            }
        }

        private async Task ApplyModeAndParametersAsync()
        {
            // 1) 写模式码（若 PLC 未使用该寄存器，可保持默认或由 PLC 方忽略）
            short modeCode = _mode switch
            {
                TrialMode.ManualP22Constant => ModeCode_P22,
                TrialMode.ManualP24RecipIntermittent => ModeCode_P24,
                TrialMode.ManualP26Step => ModeCode_P26,
                TrialMode.AutoProgram1 => ModeCode_Auto1,
                TrialMode.AutoProgram2 => ModeCode_Auto2,
                TrialMode.Jog => ModeCode_Jog,
                TrialMode.LoadSystem => ModeCode_Jog,
                _ => ModeCode_P22
            };
            await WriteInt16HoldingByKeyAsync("Param_ModeSwitch", modeCode).ConfigureAwait(false);

            // 2) 写通用加减速度（按模式）
            if (_mode == TrialMode.Jog || _mode == TrialMode.LoadSystem || _mode == TrialMode.ManualP22Constant ||
                _mode == TrialMode.ManualP24RecipIntermittent )
            {
                await WriteFloat32HoldingByKeyAsync("Param_JogAcc", _jogAccSet).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Param_JogDec", _jogDecSet).ConfigureAwait(false);
            }

            if (_mode == TrialMode.ManualP26Step ||
                _mode == TrialMode.AutoProgram1 ||
                _mode == TrialMode.AutoProgram2)
            {
                await WriteFloat32HoldingByKeyAsync("Param_AutoAcc", _autoAccSet).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Param_AutoDec", _autoDecSet).ConfigureAwait(false);
            }
            // 点动速度（若点表缺失会抛错，便于联调早发现）
            if (_mode == TrialMode.Jog)
            {
                await WriteFloat32HoldingByKeyAsync("Param_BallJogSpeed", _ballJogSpeedSet).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Param_DiskJogSpeed", _diskJogSpeedSet).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Param_PressJogSpeed", _pressJogSpeedSet).ConfigureAwait(false);
            }


            // 3) 写各模式参数
            if (_mode == TrialMode.ManualP22Constant || _mode == TrialMode.Jog)
            {
                await WriteFloat32HoldingByKeyAsync("P22_BallSpeed", _ballSpeedSet).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P22_DiskSpeed", _diskSpeedSet).ConfigureAwait(false);

                // 恒速（P22）：球/盘分别设定运行时间（点表：P22_BallTime=MD605/1210，P22_DiskTime=MD607/1214）
                double tBall = (_mode == TrialMode.Jog) ? 0 : _p22BallWorkTimeSet;
                double tDisk = (_mode == TrialMode.Jog) ? 0 : _p22DiskWorkTimeSet;
                await WriteFloat32HoldingByKeyAsync("P22_BallTime", tBall).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P22_DiskTime", tDisk).ConfigureAwait(false);
            }
            else if (_mode == TrialMode.ManualP24RecipIntermittent)
            {
                // 【修改说明】针对 PLC 遇到 0 速度直接跳过/停止的问题，发送一个极小值(0.001)代替 0，
                // 欺骗 PLC 保持运行状态，从而实现“间歇/停顿”计时。
                const double Epsilon = 0.001;

                double FixZero(double v) => Math.Abs(v) < 1e-6 ? Epsilon : v;

                // 往复 / 间歇：对速度 0 做特殊处理
                await WriteFloat32HoldingByKeyAsync("P24_BallSpeed1", FixZero(_ballSpeedSet)).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P24_BallSpeed2", FixZero(_ballSpeed2Set)).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P24_DiskSpeed1", FixZero(_diskSpeedSet)).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P24_DiskSpeed2", FixZero(_diskSpeed2Set)).ConfigureAwait(false);

                await WriteFloat32HoldingByKeyAsync("P24_BallTime1", _stageTime1Set).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P24_BallTime2", _stageTime2Set).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P24_DiskTime1", _stageTime1Set).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("P24_DiskTime2", _stageTime2Set).ConfigureAwait(false);
            }
            else if (_mode == TrialMode.ManualP26Step)
            {
                // 定义 Epsilon 策略 (与 P24 模式一致)
                const double Epsilon = 0.001;
                double FixZero(double v) => Math.Abs(v) < 1e-6 ? Epsilon : v;

                // 阶梯：按 StepCount 前 N 行下发，其余清零，避免沿用上次残留
                int n = Math.Max(1, Math.Min(16, _p26StepCount));

                // 启动前校验（只校验前 N 行）
                var err = ValidateP26TableForStart(n);
                if (!string.IsNullOrWhiteSpace(err))
                {
                    UiMessageRequested?.Invoke("阶梯参数错误", err);
                    throw new InvalidOperationException(err);
                }

                for (int i = 1; i <= 16; i++)
                {
                    double bs = 0, ds = 0, ts = 0, ln = 0;
                    if (i <= n)
                    {
                        var row = P26Steps[i - 1];
                        // 应用伪零值策略：仅针对用户输入的有效段
                        bs = FixZero(row.BallSpeed);
                        ds = FixZero(row.DiskSpeed);
                        ts = row.TimeSeconds;
                        ln = row.LoadN;
                    }
                    else
                    {
                        // 无效段维持 0，确保 PLC 跳过或不执行
                        bs = 0; ds = 0; ts = 0; ln = 0;
                    }

                    await WriteFloat32HoldingByKeyAsync($"P26_BallSpeed{i}", bs).ConfigureAwait(false);
                    await WriteFloat32HoldingByKeyAsync($"P26_DiskSpeed{i}", ds).ConfigureAwait(false);
                    await WriteFloat32HoldingByKeyAsync($"P26_Time{i}", ts).ConfigureAwait(false);
                    await WriteFloat32HoldingByKeyAsync($"P26_Load{i}", ln).ConfigureAwait(false);
                }
            }
            else if (_mode == TrialMode.AutoProgram1)
            {
                await WriteInt16HoldingByKeyAsync("Auto1_CycleCount", (short)_auto1CycleCount).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto1_TestLoad", _auto1TestLoad).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto1_PhotoLoad", _auto1PhotoLoad).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto1_WorkTime", _auto1WorkTime).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto1_BallSpeed", _auto1BallSpeed).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto1_DiskSpeed", _auto1DiskSpeed).ConfigureAwait(false);
            }
            
else if (_mode == TrialMode.AutoProgram2)
            {
                await WriteInt16HoldingByKeyAsync("Auto2_CycleCount", (short)_auto2CycleCount).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto2_TestLoad", _auto2TestLoad).ConfigureAwait(false);

                int stageMs = (int)Math.Round(_auto2StageTimeSeconds * 1000.0, MidpointRounding.AwayFromZero);
                if (stageMs < 0) stageMs = 0;
                await WriteInt32HoldingByKeyAsync("Auto2_StageTime", stageMs).ConfigureAwait(false);

                await WriteFloat32HoldingByKeyAsync("Auto2_DiskMaxSpeed", _auto2DiskMaxSpeed).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto2_DiskMinSpeed", _auto2DiskMinSpeed).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Auto2_SRR", _auto2Srr).ConfigureAwait(false);
            }

            // 下压设定载荷（MD680 / 1360）：无论模式，若 PLC 用得到可先下发
            // 设定载荷：除“阶梯/自动：恒速载荷/自动：变速”外，其余模式使用 MD680/1360（Press_LoadSet）。
            // 对于阶梯/自动模式，载荷使用各自的“设定载荷1-16 / 实验载荷 / 拍照载荷”等寄存器。
            if (_mode != TrialMode.ManualP26Step && _mode != TrialMode.AutoProgram1 && _mode != TrialMode.AutoProgram2)
                await WriteFloat32HoldingByKeyAsync("Press_LoadSet", _loadNSet).ConfigureAwait(false);
        }


#region ========== Auto2（SingleDone 点位平均 / HMI 自算 avg / 回写 DataDone 脉冲） ==========
private static bool IsFinite(double v) => !(double.IsNaN(v) || double.IsInfinity(v));

private static List<(double disk, double ball)> BuildAuto2Targets(int points, double diskMax, double diskMin, double srr)
{
    var list = new List<(double disk, double ball)>();
    if (points <= 0) points = 1;

    if (double.IsNaN(diskMax) || double.IsInfinity(diskMax)) diskMax = 0;
    if (double.IsNaN(diskMin) || double.IsInfinity(diskMin)) diskMin = diskMax;

    if (diskMax < diskMin) { var tmp = diskMax; diskMax = diskMin; diskMin = tmp; }

    double denom = 2.0 + srr;

    double DiskToBall(double d)
    {
        if (Math.Abs(denom) < 1e-9) return double.NaN;
        return d * (2.0 - srr) / denom;
    }

    if (points == 1)
    {
        var d = diskMax;
        list.Add((d, DiskToBall(d)));
        return list;
    }

    double step = (diskMax - diskMin) / (points - 1);
    for (int i = 0; i < points; i++)
    {
        double d = diskMax - i * step;
        if (i == points - 1) d = diskMin; // 保证最后一个点精确等于 min
        list.Add((d, DiskToBall(d)));
    }
    return list;
}


private static bool WithinTol(double actual, double target)
{
    if (!IsFinite(actual) || !IsFinite(target)) return false;
    double tol = Math.Max(Auto2ReachAbsTol, Auto2ReachRelTol * Math.Abs(target));
    return Math.Abs(actual - target) <= tol;
}

private (double disk, double ball) GetAuto2TargetForCurrentPoint()
{
    if (_auto2CurrentPointIndex <= 0 || _auto2CurrentPointIndex > _auto2Targets.Count)
        return (double.NaN, double.NaN);
    return _auto2Targets[_auto2CurrentPointIndex - 1];
}
private void RecomputeAuto2EnterThresholds()
{
    // 入口阈值根据“本次 Auto2 设定的最大速度”计算（目标仅用于阈值/显示，主流程不依赖达速）
    double diskMaxAbs = 0.0;
    double ballMaxAbs = 0.0;
    if (_auto2Targets != null && _auto2Targets.Count > 0)
    {
        diskMaxAbs = _auto2Targets.Max(x => Math.Abs(x.disk));
        ballMaxAbs = _auto2Targets.Max(x => Math.Abs(x.ball));
    }
    else
    {
        diskMaxAbs = Math.Max(Math.Abs(_auto2DiskMaxSpeed), Math.Abs(_auto2DiskMinSpeed));
        // ball 由 SRR 推算（尽量不为 0）
        double denom = 2.0 + _auto2Srr;
        if (Math.Abs(denom) > 1e-9)
            ballMaxAbs = diskMaxAbs * Math.Abs((2.0 - _auto2Srr) / denom);
    }

    _auto2EnterDiskThreshold = Math.Max(Auto2EnterAbsDisk, Auto2EnterRel * diskMaxAbs);
    _auto2EnterBallThreshold = Math.Max(Auto2EnterAbsBall, Auto2EnterRel * ballMaxAbs);
}

private void Auto2PushSpeedHistory(double tSeconds, double diskMmps, double ballMmps)
{
    if (!IsFinite(diskMmps) || !IsFinite(ballMmps)) return;

    _auto2SpeedHist.Enqueue((tSeconds, diskMmps, ballMmps));
    while (_auto2SpeedHist.Count > 0)
    {
        var head = _auto2SpeedHist.Peek();
        if (tSeconds - head.t <= Auto2StableWindowSeconds) break;
        _auto2SpeedHist.Dequeue();
    }
}

private bool Auto2AboveEntrySpeed(double diskMmps, double ballMmps)
{
    if (!IsFinite(diskMmps) || !IsFinite(ballMmps)) return false;
    return (Math.Abs(diskMmps) >= _auto2EnterDiskThreshold) || (Math.Abs(ballMmps) >= _auto2EnterBallThreshold);
}

private bool Auto2StableByHistory()
{
    // 稳定判据：最近窗口内 max-min 小于 band（band 取绝对/相对中的更大者）
    int n = _auto2SpeedHist.Count;
    if (n < 4) return false;

    double minD = double.PositiveInfinity, maxD = double.NegativeInfinity, sumD = 0.0;
    double minB = double.PositiveInfinity, maxB = double.NegativeInfinity, sumB = 0.0;

    foreach (var s in _auto2SpeedHist)
    {
        minD = Math.Min(minD, s.disk); maxD = Math.Max(maxD, s.disk); sumD += s.disk;
        minB = Math.Min(minB, s.ball); maxB = Math.Max(maxB, s.ball); sumB += s.ball;
    }

    double meanD = sumD / n;
    double meanB = sumB / n;
    double rangeD = maxD - minD;
    double rangeB = maxB - minB;

    double bandD = Math.Max(Auto2StableAbsBand, Auto2StableRelBand * Math.Abs(meanD));
    double bandB = Math.Max(Auto2StableAbsBand, Auto2StableRelBand * Math.Abs(meanB));

    return rangeD <= bandD && rangeB <= bandB;
}

private bool IsPlcWriteReadyNoUi()
{
    return _plcConnected && _pointMap != null;
}

private void Auto2WriteDataDoneNoUi(bool value)
{
    _ = Task.Run(async () =>
    {
        try
        {
            if (!IsPlcWriteReadyNoUi()) return;
            await WriteCommandCompatByKeyAsync("Cmd_Auto2_DataDone", value).ConfigureAwait(false);
        }
        catch
        {
            // ignore：避免写失败影响主流程；WaitingAckClear 会周期性重写 1
        }
    });
}



private void StartAuto2EffectiveWindow(double tNowSeconds)
{
    _auto2Phase = Auto2RunPhase.Recording;
    _auto2RecordingOpenSeconds = tNowSeconds;
    _auto2PointStartSeconds = tNowSeconds;

    _auto2StableStartSeconds = double.NaN;
    _auto2WindowFull = false;

    _auto2LastAccumSeconds = double.NaN;
    _auto2AccumSeconds = 0.0;

    _auto2ForceIntegral = 0.0;
    _auto2LoadIntegral = 0.0;
    _auto2MuIntegral = 0.0;
    _auto2MuSeconds = 0.0;
    _auto2BallIntegral = 0.0;
    _auto2DiskIntegral = 0.0;

    _auto2PointAvgForceN = double.NaN;
    _auto2PointAvgMu = double.NaN;
}

private void ComputeAuto2FallbackAverages(
    int pointIndex,
    double tEndSeconds,
    out double avgForce,
    out double avgMu,
    out double avgLoad,
    out double avgBall,
    out double avgDisk,
    out double effSeconds)
{
    avgForce = avgMu = avgLoad = avgBall = avgDisk = double.NaN;
    effSeconds = 0.0;

    // 兜底：用 raw 的同点位序列做 dt 加权平均
    // - 仅使用速度明显>0 的区间（排除加载阶段）
    // - 默认只取“结束前 StageTime 秒”窗口，避免把过早的加速/等待段算进去
    var all = _auto2Raw.Where(r => r.PointIndex == pointIndex)
                       .Where(r => r.TimeSeconds <= tEndSeconds + 1e-6)
                       .OrderBy(r => r.TimeSeconds)
                       .ToList();
    if (all.Count < 2) return;

    double win = Math.Max(0.5, _auto2StageTimeSeconds);
    double tStart = tEndSeconds - win;

    // 过滤：窗口内 + 速度进入循环阈值
    var samples = all.Where(r => r.TimeSeconds >= tStart - 1e-6)
                     .Where(r => Auto2AboveEntrySpeed(r.DiskSpeedMmps, r.BallSpeedMmps))
                     .ToList();
    if (samples.Count < 2) return;

    double forceInt = 0, loadInt = 0, ballInt = 0, diskInt = 0, tInt = 0;
    double muInt = 0, muT = 0;

    for (int i = 1; i < samples.Count; i++)
    {
        var a = samples[i - 1];
        var b = samples[i];
        double dt = b.TimeSeconds - a.TimeSeconds;
        if (dt <= 1e-6 || dt > 2.0) continue;

        if (IsFinite(a.TractionForceN)) forceInt += dt * a.TractionForceN;
        if (IsFinite(a.LoadN)) loadInt += dt * a.LoadN;
        if (IsFinite(a.BallSpeedMmps)) ballInt += dt * a.BallSpeedMmps;
        if (IsFinite(a.DiskSpeedMmps)) diskInt += dt * a.DiskSpeedMmps;

        if (IsFinite(a.Mu))
        {
            muInt += dt * a.Mu;
            muT += dt;
        }

        tInt += dt;
    }

    if (tInt > 1e-9)
    {
        effSeconds = tInt;
        avgForce = forceInt / tInt;
        avgLoad = loadInt / tInt;
        avgBall = ballInt / tInt;
        avgDisk = diskInt / tInt;
        avgMu = muT > 1e-9 ? (muInt / muT) : double.NaN;
    }
}


private void ResetAuto2Accumulators(double nowSeconds)
{
    // 新点位：先进入“等待稳定”，只有检测到速度进入并稳定后才打开统计窗
    _auto2Phase = Auto2RunPhase.WaitStable;

    _auto2SpeedHist.Clear();
    _auto2StableStartSeconds = double.NaN;
    _auto2RecordingOpenSeconds = double.NaN;
    _auto2WindowFull = false;

    _auto2PointStartSeconds = double.NaN;
    _auto2LastAccumSeconds = double.NaN;
    _auto2AccumSeconds = 0.0;

    _auto2ForceIntegral = 0.0;
    _auto2LoadIntegral = 0.0;
    _auto2MuIntegral = 0.0;
    _auto2MuSeconds = 0.0;
    _auto2BallIntegral = 0.0;
    _auto2DiskIntegral = 0.0;

    _auto2PointAvgForceN = double.NaN;
    _auto2PointAvgMu = double.NaN;

    _auto2AckActive = false;
    _auto2AckStartSeconds = double.NaN;
    _auto2AckLastWriteSeconds = double.NaN;
}


private void BeginAuto2Run(double nowSeconds)
{
    _auto2RunActive = true;
    _auto2RunFrozen = false;

    _auto2Raw.Clear();
    _auto2Summary.Clear();

    _auto2Targets = BuildAuto2Targets(_auto2CycleCount, _auto2DiskMaxSpeed, _auto2DiskMinSpeed, _auto2Srr);
    _auto2CurrentPointIndex = 1;

    RecomputeAuto2EnterThresholds();

    // 启动后先进入“加载”阶段：此时球盘速度≈0，不应计入点位统计
    _auto2Phase = Auto2RunPhase.Loading;
    _auto2StateText = "Auto2：加载中";

    _auto2SpeedHist.Clear();
    _auto2StableStartSeconds = double.NaN;
    _auto2RecordingOpenSeconds = double.NaN;
    _auto2WindowFull = false;

    _auto2PointStartSeconds = double.NaN;
    _auto2LastAccumSeconds = double.NaN;
    _auto2AccumSeconds = 0.0;

    _auto2ForceIntegral = 0.0;
    _auto2LoadIntegral = 0.0;
    _auto2MuIntegral = 0.0;
    _auto2MuSeconds = 0.0;
    _auto2BallIntegral = 0.0;
    _auto2DiskIntegral = 0.0;

    _auto2PointAvgForceN = double.NaN;
    _auto2PointAvgMu = double.NaN;

    _auto2AckActive = false;
    _auto2AckStartSeconds = double.NaN;
    _auto2AckLastWriteSeconds = double.NaN;

    _auto2UnloadWaitStartSeconds = double.NaN;
    _auto2UnloadStableStartSeconds = double.NaN;

    // 保险：启动前先把“数据记录完成(322)”清 0，避免上一次遗留
    Auto2WriteDataDoneNoUi(false);

    OnPropertyChanged(nameof(Auto2StateText));
    OnPropertyChanged(nameof(Auto2PointProgressText));
    OnPropertyChanged(nameof(Auto2CurrentTargetText));
    OnPropertyChanged(nameof(Auto2EffectiveTimeText));
    OnPropertyChanged(nameof(Auto2AvgForceText));
    OnPropertyChanged(nameof(Auto2AvgMuText));
}

private void StopAuto2Run(string reason)
{
    if (!_auto2RunActive && !_auto2RunFrozen) return;

    _auto2RunActive = false;
    _auto2RunFrozen = false;

    _auto2Phase = Auto2RunPhase.Idle;

    _auto2SpeedHist.Clear();
    _auto2StableStartSeconds = double.NaN;
    _auto2RecordingOpenSeconds = double.NaN;
    _auto2WindowFull = false;

    _auto2PointStartSeconds = double.NaN;
    _auto2LastAccumSeconds = double.NaN;
    _auto2AccumSeconds = 0.0;

    _auto2ForceIntegral = 0.0;
    _auto2LoadIntegral = 0.0;
    _auto2MuIntegral = 0.0;
    _auto2MuSeconds = 0.0;
    _auto2BallIntegral = 0.0;
    _auto2DiskIntegral = 0.0;

    _auto2AckActive = false;
    _auto2AckStartSeconds = double.NaN;
    _auto2AckLastWriteSeconds = double.NaN;

    _auto2UnloadWaitStartSeconds = double.NaN;
    _auto2UnloadStableStartSeconds = double.NaN;

    // 停止时确保 322 释放（由上位机写回 0）
    Auto2WriteDataDoneNoUi(false);

    _auto2StateText = "Auto2：已停止";

    OnPropertyChanged(nameof(Auto2StateText));
    OnPropertyChanged(nameof(Auto2PointProgressText));
    OnPropertyChanged(nameof(Auto2CurrentTargetText));
    OnPropertyChanged(nameof(Auto2EffectiveTimeText));
    OnPropertyChanged(nameof(Auto2AvgForceText));
    OnPropertyChanged(nameof(Auto2AvgMuText));
}

/// <summary>
/// Auto2：每次 UI Tick（20Hz）累积 dt×值，得到“本点平均值”
/// </summary>
private void UpdateAuto2OnTick(double tNowSeconds)
{
    if (!_auto2RunActive || _auto2RunFrozen) return;
    if (_mode != TrialMode.AutoProgram2 || _runState != RunState.Running) return;

    int idx = Math.Max(1, _auto2CurrentPointIndex);

    // 速度反馈：用于“加载/循环”状态推断与稳定判定（不依赖目标达速）
    double diskMmps = _plcDiskSpeedRaw;
    double ballMmps = _plcBallSpeedRaw;

    Auto2PushSpeedHistory(tNowSeconds, diskMmps, ballMmps);

    bool aboveEntry = Auto2AboveEntrySpeed(diskMmps, ballMmps);
    bool stableNow = aboveEntry && Auto2StableByHistory();

    // —— 状态机：Loading -> WaitStable -> Recording -> WaitingAckClear -> (next) -> UnloadingWait -> Finished ——
    if (_auto2Phase == Auto2RunPhase.Loading)
    {
        _auto2StateText = "Auto2：加载中";
        // 加载结束后 PLC 会直接进入第 1 循环：此时速度会明显上升
        if (aboveEntry)
        {
            _auto2Phase = Auto2RunPhase.WaitStable;
            _auto2StableStartSeconds = double.NaN;
            _auto2StateText = "Auto2：进入循环，等待稳定";
        }
    }

    if (_auto2Phase == Auto2RunPhase.WaitStable)
    {
        // 等待速度“进入循环”并稳定
        if (!aboveEntry)
        {
            _auto2StableStartSeconds = double.NaN;
            _auto2StateText = (idx == 1) ? "Auto2：加载中（等待进入第1循环）" : "Auto2：等待进入循环";
        }
        else if (stableNow)
        {
            if (double.IsNaN(_auto2StableStartSeconds))
                _auto2StableStartSeconds = tNowSeconds;
            else if (tNowSeconds - _auto2StableStartSeconds >= Auto2SettleSeconds)
                StartAuto2EffectiveWindow(tNowSeconds);
            _auto2StateText = "Auto2：等待稳定";
        }
        else
        {
            _auto2StableStartSeconds = double.NaN;
            _auto2StateText = "Auto2：等待稳定";
        }


        // 若 PLC 已经置位 SingleDone（保持位），即使稳定判据未满足也必须及时握手，避免 PLC 卡住
        if (_plcAuto2SingleDone && !_auto2AckActive)
        {
            OnAuto2SingleDonePulse(tNowSeconds);
            OnPropertyChanged(nameof(Auto2StateText));
            return;
        }
    }

    // raw（用于导出）：在 WaitStable/Recording 期间记录（不记录 Loading=速度≈0，避免误导）
    if (_auto2Phase == Auto2RunPhase.WaitStable || _auto2Phase == Auto2RunPhase.Recording)
    {
        _auto2Raw.Add(new Auto2RawSample
        {
            TimeSeconds = tNowSeconds,
            PointIndex = idx,
            SerialLoadRaw = _serialLoadRaw,
            LoadN = _loadN,
            TractionForceN = _tractionForceN,
            Mu = _mu,
            BallSpeedMmps = ballMmps,
            DiskSpeedMmps = diskMmps,
            RollingSpeedMmps = _rollingSpeed,
            Srr = _srr,
            TemperatureC = _temperatureC
        });
    }

    if (_auto2Phase == Auto2RunPhase.Recording)
    {
        _auto2StateText = _auto2WindowFull ? "Auto2：记录窗已满（等待点结束）" : "Auto2：记录中";

        // 只保留“速度稳定”作为有效窗判据：
        // - 不再用载荷达标/载荷阈值作为开窗条件（载荷低也照样计时/采样）
        // - 仍要求速度数据本身有效（finite），避免 NaN/Inf 污染积分
        bool valid = stableNow && IsFinite(diskMmps) && IsFinite(ballMmps);

        if (!_auto2WindowFull)
        {
            double stageT = Math.Max(0.1, _auto2StageTimeSeconds);

            if (valid)
            {
                if (double.IsNaN(_auto2LastAccumSeconds))
                    _auto2LastAccumSeconds = tNowSeconds;

                double dt = tNowSeconds - _auto2LastAccumSeconds;
                _auto2LastAccumSeconds = tNowSeconds;

                // 防止暂停/切后台导致 dt 过大污染积分
                if (dt > 1e-6 && dt < 2.0)
                {
                    double remaining = Math.Max(0.0, stageT - _auto2AccumSeconds);
                    double dtUse = Math.Min(dt, remaining);

                    if (dtUse > 1e-9)
                    {
                        _auto2AccumSeconds += dtUse;
                        // 注意：有效窗只由“速度稳定”决定。
                        // 但为了避免传感器偶发 NaN/Inf 把积分污染成 NaN，这里对各通道做 finite 保护。
                        if (IsFinite(_tractionForceN)) _auto2ForceIntegral += dtUse * _tractionForceN;
                        if (IsFinite(_loadN)) _auto2LoadIntegral += dtUse * _loadN;

                        if (IsFinite(_mu))
                        {
                            _auto2MuIntegral += dtUse * _mu;
                            _auto2MuSeconds += dtUse;
                        }

                        if (IsFinite(ballMmps)) _auto2BallIntegral += dtUse * ballMmps;
                        if (IsFinite(diskMmps)) _auto2DiskIntegral += dtUse * diskMmps;
                    }

                    if (_auto2AccumSeconds >= stageT - 1e-6)
                        _auto2WindowFull = true;
                }
            }
            else
            {
                // 不稳定/无效数据段不计入有效时间
                _auto2LastAccumSeconds = double.NaN;
            }
        }

        // 实时显示本点平均
        if (_auto2AccumSeconds > 1e-9)
        {
            _auto2PointAvgForceN = _auto2ForceIntegral / _auto2AccumSeconds;
            _auto2PointAvgMu = _auto2MuSeconds > 1e-9 ? (_auto2MuIntegral / _auto2MuSeconds) : double.NaN;
        }

        OnPropertyChanged(nameof(Auto2EffectiveTimeText));
        OnPropertyChanged(nameof(Auto2AvgForceText));
        OnPropertyChanged(nameof(Auto2AvgMuText));

        // 点结束：SingleDone 现在是“保持位”，上位机看到为 1 就结束该点并进入 322 握手
        if (_plcAuto2SingleDone && !_auto2AckActive)
        {
            OnAuto2SingleDonePulse(tNowSeconds);
            OnPropertyChanged(nameof(Auto2StateText));
            return;
        }
    }

    if (_auto2Phase == Auto2RunPhase.WaitingAckClear)
    {
        _auto2StateText = "Auto2：等待 PLC 清除 SingleDone（握手中）";

        // 周期性重写 322=1，避免写入丢包导致 PLC 不清 SingleDone
        if (double.IsNaN(_auto2AckLastWriteSeconds) || (tNowSeconds - _auto2AckLastWriteSeconds) >= Auto2AckResendPeriodSeconds)
        {
            Auto2WriteDataDoneNoUi(true);
            _auto2AckLastWriteSeconds = tNowSeconds;
        }

        // PLC 清零 SingleDone 后，写回 322=0，并推进到下一循环/卸载
        if (!_plcAuto2SingleDone && IsFinite(_auto2AckStartSeconds) && (tNowSeconds - _auto2AckStartSeconds) >= Auto2AckMinHoldSeconds)
        {
            Auto2WriteDataDoneNoUi(false);

            _auto2AckActive = false;
            _auto2AckStartSeconds = double.NaN;
            _auto2AckLastWriteSeconds = double.NaN;

            if (idx >= _auto2CycleCount)
            {
                _auto2Phase = Auto2RunPhase.UnloadingWait;
                _auto2UnloadWaitStartSeconds = tNowSeconds;
                _auto2UnloadStableStartSeconds = double.NaN;
                _auto2StateText = "Auto2：卸载中";
            }
            else
            {
                _auto2CurrentPointIndex = idx + 1;
                ResetAuto2Accumulators(tNowSeconds);
                _auto2StateText = "Auto2：等待稳定";
            }

            OnPropertyChanged(nameof(Auto2PointProgressText));
            OnPropertyChanged(nameof(Auto2CurrentTargetText));
            OnPropertyChanged(nameof(Auto2EffectiveTimeText));
            OnPropertyChanged(nameof(Auto2AvgForceText));
            OnPropertyChanged(nameof(Auto2AvgMuText));
        }
    }

    if (_auto2Phase == Auto2RunPhase.UnloadingWait)
    {
        _auto2StateText = "Auto2：卸载中";

        // 卸载完成判据：速度接近 0 且持续一段时间
        bool lowSpeed = IsFinite(diskMmps) && IsFinite(ballMmps) && Math.Abs(diskMmps) < 5.0 && Math.Abs(ballMmps) < 5.0;

        if (lowSpeed)
        {
            if (double.IsNaN(_auto2UnloadStableStartSeconds))
                _auto2UnloadStableStartSeconds = tNowSeconds;
            else if (tNowSeconds - _auto2UnloadStableStartSeconds >= Auto2UnloadingDetectSeconds)
            {
                _auto2RunActive = false;
                _auto2RunFrozen = true;
                _auto2Phase = Auto2RunPhase.Finished;
                _auto2StateText = "Auto2：完成";

                OnPropertyChanged(nameof(Auto2StateText));
                OnPropertyChanged(nameof(Auto2PointProgressText));
                OnPropertyChanged(nameof(Auto2CurrentTargetText));
                return;
            }
        }
        else
        {
            _auto2UnloadStableStartSeconds = double.NaN;
        }

        // 超时兜底：卸载迟迟不结束，尝试 TotalStop，避免“程序不停止”
        if (IsFinite(_auto2UnloadWaitStartSeconds) && (tNowSeconds - _auto2UnloadWaitStartSeconds) >= Auto2UnloadingMaxWaitSeconds)
        {
            _auto2UnloadWaitStartSeconds = double.PositiveInfinity; // 只触发一次

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!IsPlcWriteReadyNoUi()) return;
                    await PulseCommandCompatByKeyAsync("Cmd_TotalStop", CommandPulseMs).ConfigureAwait(false);
                }
                catch { /* ignore */ }
            });

            _auto2RunActive = false;
            _auto2RunFrozen = true;
            _auto2Phase = Auto2RunPhase.Finished;
            _auto2StateText = "Auto2：卸载超时（已尝试停机）";

            OnPropertyChanged(nameof(Auto2StateText));
            OnPropertyChanged(nameof(Auto2PointProgressText));
            OnPropertyChanged(nameof(Auto2CurrentTargetText));
            return;
        }
    }

    OnPropertyChanged(nameof(Auto2StateText));
    OnPropertyChanged(nameof(Auto2PointProgressText));
    OnPropertyChanged(nameof(Auto2CurrentTargetText));
}


/// <summary>
/// Auto2：PLC 回读 SingleDone 上升沿 -> 结束当前点位，写入 summary，并回写 DataDone 脉冲
/// </summary>
private void OnAuto2SingleDonePulse(double tNowSeconds)
{
    // SingleDone 已改为“保持置 1，直到收到 322 才清 0”，因此：
    // - 上位机看到 SingleDone==1 即结束当前点
    // - 写 322=1 并保持，直到观察到 SingleDone 清 0，再写回 322=0
    if (!_auto2RunActive || _auto2RunFrozen) return;
    if (_mode != TrialMode.AutoProgram2) return;

    int idx = Math.Max(1, _auto2CurrentPointIndex);
    (double disk, double ball) target = (idx >= 1 && idx <= _auto2Targets.Count)
        ? _auto2Targets[idx - 1]
        : (double.NaN, double.NaN);

    double eff = _auto2AccumSeconds;

    double avgForce = eff > 1e-9 ? (_auto2ForceIntegral / eff) : double.NaN;
    double avgLoad = eff > 1e-9 ? (_auto2LoadIntegral / eff) : double.NaN;
    double avgBall = eff > 1e-9 ? (_auto2BallIntegral / eff) : double.NaN;
    double avgDisk = eff > 1e-9 ? (_auto2DiskIntegral / eff) : double.NaN;

    double avgMu = _auto2MuSeconds > 1e-9 ? (_auto2MuIntegral / _auto2MuSeconds) : double.NaN;

    // 兜底：有效窗太短或关键量无效 -> 用 raw 的同点位序列做 dt 加权平均（尽量排除速度≈0的加载段）
    if (eff < Auto2MinEffectiveSeconds || double.IsNaN(avgForce))
    {
        ComputeAuto2FallbackAverages(idx, tNowSeconds, out var f, out var mu, out var ld, out var bb, out var dd, out var teff);
        if (teff > eff)
        {
            eff = teff;
            avgForce = f;
            avgMu = mu;
            avgLoad = ld;
            avgBall = bb;
            avgDisk = dd;
        }
    }

    _auto2PointAvgForceN = avgForce;
    _auto2PointAvgMu = avgMu;

    double startS = IsFinite(_auto2PointStartSeconds) ? _auto2PointStartSeconds :
                    (IsFinite(_auto2RecordingOpenSeconds) ? _auto2RecordingOpenSeconds : tNowSeconds);

    _auto2Summary.Add(new Auto2PointSummary
    {
        PointIndex = idx,
        TargetDiskMmps = target.disk,
        TargetBallMmps = target.ball,
        TargetSrr = _auto2Srr,
        StageTimeSeconds = _auto2StageTimeSeconds,
        StartSeconds = startS,
        EndSeconds = tNowSeconds,
        EffectiveSeconds = eff,
        AvgTractionForceN = avgForce,
        AvgMu = avgMu,
        AvgLoadN = avgLoad,
        AvgBallSpeedMmps = avgBall,
        AvgDiskSpeedMmps = avgDisk
    });

    // 进入握手：写 322=1 并等待 PLC 清 SingleDone
    _auto2Phase = Auto2RunPhase.WaitingAckClear;
    _auto2AckActive = true;
    _auto2AckStartSeconds = tNowSeconds;
    _auto2AckLastWriteSeconds = double.NaN;

    // 停止累计，避免 SingleDone 置 1 后继续积分
    _auto2WindowFull = true;
    _auto2LastAccumSeconds = double.NaN;

    Auto2WriteDataDoneNoUi(true);

    _auto2StateText = "Auto2：点结束（握手中）";

    OnPropertyChanged(nameof(Auto2StateText));
    OnPropertyChanged(nameof(Auto2PointProgressText));
    OnPropertyChanged(nameof(Auto2CurrentTargetText));
    OnPropertyChanged(nameof(Auto2EffectiveTimeText));
    OnPropertyChanged(nameof(Auto2AvgForceText));
    OnPropertyChanged(nameof(Auto2AvgMuText));
}

#endregion

        /// <summary>
        /// 等待串口给出一次有效载荷值（用于 PLC 侧闭环/停机判据的回写）。
        /// 若超时仍无有效数据，说明串口未初始化/未运行/通道无效，此时禁止启动自动程序以避免 PLC 持续下压不止。
        /// </summary>
        private async Task<bool> WaitForValidLoadFeedbackAsync(int timeoutMs = 1500)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (_strainService.TryGetLatest(out var loadRaw, out _))
                    {
                        // 校验“标定后的载荷”是否为有限值，避免用户把 Scale 填成 NaN/Inf 导致回写失效
                        var loadN = (loadRaw + _serialLoadOffset) * _serialLoadScale;
                        if (!double.IsNaN(loadN) && !double.IsInfinity(loadN))
                            return true;
                    }
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
            return false;
        }
        private async void Start()
        {
            if (!EnsurePlcWriteReady()) return;
            if (_runState == RunState.Fault || _runState == RunState.EStop)
            {
                SetAlarm("Alarm: Need reset");
                return;
            }
            // 试验启动时若未点“采集开始”，自动开启采集链路（用于 PLC 侧闭环/报警所需的测量回写）
            if (!UseSimulatedSource && !_isAcquiring)
            {
                try { StartAcquisition(); }
                catch (Exception ex) { SetAlarm("Alarm: StartAcq failed - " + ex.Message); }
            }


            // 自动程序/加载系统依赖 PC->PLC 的载荷回写做闭环：必须确认已获得有效载荷数据，否则 PLC 可能持续下压不止
            if (!UseSimulatedSource && (_mode == TrialMode.AutoProgram1 || _mode == TrialMode.AutoProgram2 || _mode == TrialMode.LoadSystem))
            {
                bool ok = await WaitForValidLoadFeedbackAsync(1500).ConfigureAwait(false);
                if (!ok)
                {
                    SetAlarm("Alarm: No valid Load feedback (Serial not ready)");
                    UiMessageRequested?.Invoke("启动失败",
                        "未获得有效载荷反馈（串口/变送器/通道未就绪）。为安全起见已禁止启动自动程序：\n" +
                        "1) 先确保【采集开始】后 Serial 状态为 OK，Load 不为“--”；\n" +
                        "2) 在 Modbus Poll 观察 4x1380（MeasHmi_LoadN，载荷回写）随载荷变化。\n" +
                        "若 4x1380 不变化，PLC 侧不会停止下压。");
                    return;
                }
            }
            
                        // Auto2：启动前初始化 HMI 点位平均与导出缓存
            if (_mode == TrialMode.AutoProgram2)
                BeginAuto2Run(_sw.Elapsed.TotalSeconds);
            else
                StopAuto2Run("not auto2");

            try
            {
                await ApplyModeAndParametersAsync().ConfigureAwait(false);

                // Auto1（自动：恒速载荷）：参数下发后，等待 PLC 扫描/锁存再启动。
                // 目的：避免“参数刚写入就触发 Start，PLC 尚未读到新参数”的现场问题。
                if (_mode == TrialMode.AutoProgram1)
                    await Task.Delay(Auto1StartParamDelayMs).ConfigureAwait(false);

                // Auto2（自动：变速）：参数下发后，等待 PLC 扫描/锁存再启动.
                // 目的：避免“参数刚写入就触发 Start，上一轮参数/默认值被 PLC 读取到”的现场问题。
                if (_mode == TrialMode.AutoProgram2)
                    await Task.Delay(Auto2StartParamDelayMs).ConfigureAwait(false);


                // 启动脉冲：按模式选择对应启动位
                if (_mode == TrialMode.ManualP22Constant)
                {
                    // —— P2.2 恒速（球/盘）启动前置条件校验 —— 
                    // PLC 错误码 500 = “未上使能或者运动参数未填齐全”，此处在上位机侧提前拦截，避免触发 PLC 故障并锁死状态机。

                    bool ballValid = Math.Abs(_ballSpeedSet) > 1e-6 && _p22BallWorkTimeSet > 0;
                    bool diskValid = Math.Abs(_diskSpeedSet) > 1e-6 && _p22DiskWorkTimeSet > 0;

                    if (!ballValid && !diskValid)
                    {
                        UiMessageRequested?.Invoke("恒速(P2.2) 启动失败", "球/盘速度需≠0 且对应工作时间需>0（至少满足一轴）。");
                        return;
                    }

                    // 按项目要求：先加载后运动。若未在加载系统中达标锁存，则直接提示用户先加载。
                    // 若你们现场允许“零载荷空跑”，可将此判断改为：仅当 LoadNSet>0 才要求 _manualAppliedLoadValid。
                    if (!_manualAppliedLoadValid)
                    {
                        UiMessageRequested?.Invoke("恒速(P2.2)", "当前未完成加载系统“加载到设定载荷”。请先在【加载系统】按住“加载”至达标后，再启动恒速试验。");
                        return;
                    }

                    // 兼容：部分 PLC 在每次启动前需要一次“运动复位”清状态（否则判定参数未上传/轴未就绪）
                    await PulseCommandCompatGroupAsync(new[] { "Cmd_P22_BallReset", "Cmd_P22_DiskReset" }, 300).ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);

                    // 启动位做长一点的“按钮按下”时间，避免 PLC 扫描/监控软件刷新导致看不到脉冲
                    const int StartPulseMs = 600;

                    // 只对“参数有效”的轴下发启动，避免一轴参数为 0 触发 PLC 500 并拖累另一轴
                    var startKeys = new List<string>(2);
                    if (ballValid) startKeys.Add("Cmd_P22_BallStart");
                    if (diskValid) startKeys.Add("Cmd_P22_DiskStart");
                    await PulseCommandCompatGroupAsync(startKeys.ToArray(), StartPulseMs).ConfigureAwait(false);
                }
                else if (_mode == TrialMode.ManualP24RecipIntermittent)
                {
                    // 按项目要求：先加载后运动。若未在加载系统中达标锁存，则直接提示用户先加载。
                    // 若你们现场允许“零载荷空跑”，可将此判断改为：仅当 LoadNSet>0 才要求 _manualAppliedLoadValid。
                    if (!_manualAppliedLoadValid)
                    {
                        UiMessageRequested?.Invoke("往复/间歇(P2.4)", "当前未完成加载系统“加载到设定载荷”。请先在【加载系统】按住“加载”至达标后，再启动往复/间歇试验。");
                        return;
                    }

                    await PulseCommandCompatGroupAsync(new[] { "Cmd_P24_BallStart", "Cmd_P24_DiskStart" }, CommandPulseMs).ConfigureAwait(false);
                }
                else if (_mode == TrialMode.ManualP26Step)
                {
                    await PulseCommandCompatByKeyAsync("Cmd_P26_Reset", 200).ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);
                    await PulseCommandCompatByKeyAsync("Cmd_P26_Start", 600).ConfigureAwait(false);
                }
                else if (_mode == TrialMode.AutoProgram1)
                {
                    await PulseCommandCompatByKeyAsync("Cmd_Auto1_Start", 300).ConfigureAwait(false);
                }
                else if (_mode == TrialMode.AutoProgram2)
                {
                    await PulseCommandCompatByKeyAsync("Cmd_Auto2_Start", 300).ConfigureAwait(false);
                }

                SetRunState(RunState.Running);
                StartDaqStreamingIfPossible();
                _localAlarmText = "Alarm: None";
                _alarmDetailText = "";
                NotifyStatus();
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Start Failed - " + ex.Message);
            }
        }

        private async void Stop()
        {
            if (!_plcConnected) { SetAlarm("Alarm: PLC not connected"); return; }
            if (_runState != RunState.Running) return;
            try
            {
                SetRunState(RunState.Stopping);
                StopDaqStreaming();

                // Auto2：停止时清理内部状态
                StopAuto2Run("stop");

                await PulseCommandCompatByKeyAsync("Cmd_TotalStop", CommandPulseMs).ConfigureAwait(false);

                SetRunState(RunState.Connected);
                _localAlarmText = "Alarm: None";
                _alarmDetailText = "";
                NotifyStatus();
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Stop Failed - " + ex.Message);
            }
        }


        /// <summary>
        /// 点动：球/盘分别点动（按住开始，松开停止）。
        /// - 方向：由参数区勾选（BallJogForward/BallJogReverse，DiskJogForward/DiskJogReverse）
        /// - 触发：运行与安全区“球点动（按住）/盘点动（按住）”置位保持线圈
        /// </summary>
        public async void JogBallDown()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                SetMode(TrialMode.Jog);
                await ApplyModeAndParametersAsync().ConfigureAwait(false);

                // 先确保互斥释放
                await WriteCommandCompatByKeyAsync("Cmd_BallJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_BallJogReverseHold", false).ConfigureAwait(false);

                if (_ballJogForward)
                {
                    await WriteCommandCompatByKeyAsync("Cmd_BallJogForwardHold", true).ConfigureAwait(false);
                }
                else if (_ballJogReverse)
                {
                    await WriteCommandCompatByKeyAsync("Cmd_BallJogReverseHold", true).ConfigureAwait(false);
                }
                else
                {
                    UiMessageRequested?.Invoke("点动", "球点动未选择方向，请先勾选“球_正向点动”或“球_反向点动”。");
                }
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Jog(Ball) Failed - " + ex.Message);
            }
        }

        public async void JogBallUp()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                await WriteCommandCompatByKeyAsync("Cmd_BallJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_BallJogReverseHold", false).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        public async void JogDiskDown()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                SetMode(TrialMode.Jog);
                await ApplyModeAndParametersAsync().ConfigureAwait(false);

                // 先确保互斥释放
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogReverseHold", false).ConfigureAwait(false);

                if (_diskJogForward)
                {
                    await WriteCommandCompatByKeyAsync("Cmd_DiskJogForwardHold", true).ConfigureAwait(false);
                }
                else if (_diskJogReverse)
                {
                    await WriteCommandCompatByKeyAsync("Cmd_DiskJogReverseHold", true).ConfigureAwait(false);
                }
                else
                {
                    UiMessageRequested?.Invoke("点动", "盘点动未选择方向，请先勾选“盘_正向点动”或“盘_反向点动”。");
                }
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Jog(Disk) Failed - " + ex.Message);
            }
        }

        public async void JogDiskUp()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_DiskJogReverseHold", false).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        public async void JogPressDown()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                SetMode(TrialMode.Jog);
                await ApplyModeAndParametersAsync().ConfigureAwait(false);

                // 先确保互斥释放
                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);

                if (_pressJogForward)
                {
                    await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", true).ConfigureAwait(false);
                }
                else if (_pressJogReverse)
                {
                    await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", true).ConfigureAwait(false);
                }
                else
                {
                    UiMessageRequested?.Invoke("点动", "下压点动未选择方向，请先勾选“下压_正向点动”或“下压_反向点动”。");
                }
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Jog(Press) Failed - " + ex.Message);
            }
        }

        public async void JogPressUp()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        private bool CanPressOperation()
        {
            if (!_plcClient.IsConnected) return false;
            if (_pressOpInProgress) return false;
            if (_runState == RunState.Running || _runState == RunState.Stopping || _runState == RunState.EStop) return false;
            return true;
        }

        private bool CanPressReset()
        {
            if (!_plcClient.IsConnected) return false;
            if (_runState == RunState.EStop) return false;
            return true;
        }

        private void CancelPressOperationSilently()
        {
            try { _pressOpCts?.Cancel(); } catch { /* ignore */ }
        }

        private async void PressLoadClick()
        {
            if (!EnsurePlcWriteReady()) return;
            if (!CanPressOperation())
            {
                UiMessageRequested?.Invoke("加载系统", "当前状态不允许执行加载。请确保 PLC 已连接且不在运行/急停状态。");
                return;
            }
            await StartPressOperationAsync(isLoad: true).ConfigureAwait(false);
        }

        private async void PressUnloadClick()
        {
            if (!EnsurePlcWriteReady()) return;
            if (!CanPressOperation())
            {
                UiMessageRequested?.Invoke("加载系统", "当前状态不允许执行卸载。请确保 PLC 已连接且不在运行/急停状态。");
                return;
            }
            await StartPressOperationAsync(isLoad: false).ConfigureAwait(false);
        }

        private async void PressResetClick()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                CancelPressOperationSilently();
                _pressStartHolding = false;
                try { await WriteCommandCompatByKeyAsync("Cmd_PressStart", false).ConfigureAwait(false); } catch { /* ignore */ }
                await PulseCommandCompatByKeyAsync("Cmd_PressReset", CommandPulseMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: PressReset Failed - " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task StartPressOperationAsync(bool isLoad)
        {
            _pressOpInProgress = true;
            RefreshCommandState();

            _pressOpCts?.Dispose();
            _pressOpCts = new System.Threading.CancellationTokenSource();
            var token = _pressOpCts.Token;

            // 清理锁存，避免历史完成/失败误触发
            _plcMotionDoneLatched = false;
            _plcMotionFailLatched = false;

            var oldState = _runState;
            SetRunState(RunState.Loading);

            try
            {
                SetMode(TrialMode.LoadSystem);

                // 1) 写参数：模式 + 设定载荷
                // 先确保 Start=0（避免残留高电平）
                _pressStartHolding = false;
                try { await WriteCommandCompatByKeyAsync("Cmd_PressStart", false).ConfigureAwait(false); } catch { /* ignore */ }

                await WriteInt16HoldingByKeyAsync("Param_ModeSwitch", ModeCode_Jog).ConfigureAwait(false);
                await WriteFloat32HoldingByKeyAsync("Press_LoadSet", _loadNSet).ConfigureAwait(false);

                // 需求：下发参数后延迟 500ms 再启动
                await System.Threading.Tasks.Task.Delay(500, token).ConfigureAwait(false);

                // 2) Start=1（保持）
                _pressStartHolding = true;
                await WriteCommandCompatByKeyAsync("Cmd_PressStart", true).ConfigureAwait(false);

                // 3) 等待 PLC Done/Fail
                await WaitForPressDoneOrFailAsync(token).ConfigureAwait(false);

                // 4) 完成提示（按需求：读到 Done 高位后先提示）
                // 5) 锁存“已加载”标志，供 P2.2/P2.4 运行前置条件使用
                if (isLoad)
                {
                    _manualAppliedLoadValid = true;
                    _manualAppliedLoadN = _loadNSet;
                    OnPropertyChanged(nameof(ManualLoadSetForMotion));
                    UiMessageRequested?.Invoke("加载系统", $"加载完成，当前设定载荷为：{_loadNSet:F1}N！");
                }
                else
                {
                    _manualAppliedLoadValid = false;
                    OnPropertyChanged(nameof(ManualLoadSetForMotion));
                    UiMessageRequested?.Invoke("加载系统", $"卸载完成，当前设定载荷为：{_loadNSet:F1}N！");
                }

                // 6) Start=0（提示后撤销运动开始）
                _pressStartHolding = false;
                await WriteCommandCompatByKeyAsync("Cmd_PressStart", false).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 被复位/急停/切换模式等打断：不弹窗
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: LoadSystem Failed - " + ex.Message);
                UiMessageRequested?.Invoke("加载系统", $"{(isLoad ? "加载" : "卸载")}失败：{ex.Message}");
            }
            finally
            {
                try
                {
                    _pressStartHolding = false;
                    await WriteCommandCompatByKeyAsync("Cmd_PressStart", false).ConfigureAwait(false);
                }
                catch { /* ignore */ }

                _pressOpInProgress = false;
                _pressOpCts?.Dispose();
                _pressOpCts = null;

                if (_runState == RunState.Loading)
                    SetRunState(_plcConnected ? RunState.Connected : RunState.Idle);

                RefreshCommandState();
            }
        }

        private async System.Threading.Tasks.Task WaitForPressDoneOrFailAsync(System.Threading.CancellationToken token)
        {
            bool doneInitial = _plcMotionDone;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();

                // Fail 优先
                if (_plcMotionFailLatched || _plcMotionFail)
                    throw new InvalidOperationException("PLC 返回运动失败（St_MotionFail=1）");

                bool doneNow = _plcMotionDone;
                if (_plcMotionDoneLatched || (!doneInitial && doneNow))
                    return;

                if (doneInitial && !doneNow)
                    doneInitial = false;

                if (sw.Elapsed.TotalSeconds > 60)
                    throw new TimeoutException("等待 PLC 运动完成超时（St_MotionDone）");

                await System.Threading.Tasks.Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        // ==========================================================
        // 加载系统：加载/卸载（按住有效，松开停止）+ 双保险停机判据
        // - 载荷到设定值（来自 PCIe 载荷通道）
        // - 下压限位触发（来自 PLC 离散输入 St_PressLimit）
        // ==========================================================

        /// <summary>
        /// 加载（按住）：下压电机正向点动保持；同时打开“设定载荷触发”判据。
        /// </summary>
        public async void PressLoadDown()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                SetMode(TrialMode.LoadSystem);

                await ApplyJogSetupFromReferenceAsync().ConfigureAwait(false);

                // 为实现“载荷到设定值”判据，建议保证采集链路已启动（至少载荷通道）。
                if (!_daqService.IsRunning || !_strainService.IsRunning)
                {
                    try { StartAcquisition(); }
                    catch (Exception ex) { SetAlarm($"Alarm: StartAcq failed: {ex.Message}"); }
                }

                await ApplyModeAndParametersAsync().ConfigureAwait(false);

                _pressStopping = false;
                _pressUnloadHolding = false;
                _pressLoadHolding = true;

                _pressLimitAtStartLoad = _pressLimit;
                _pressLimitSeenFalseDuringLoad = !_pressLimit;

                // 互斥：先确保反向释放
                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);

                // 使能：载荷到设定值触发（PLC 侧可用于自动停机；上位机也会双保险判据）
                await WriteCoilByKeyAsync("Cmd_PressLoadSetTrigger", true).ConfigureAwait(false);

                // 正向点动（按住）
                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UiMessageRequested?.Invoke("加载系统", $"加载（按住）下发失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 加载松开：释放正向点动保持，并关闭设定载荷触发。
        /// </summary>
        public async void PressLoadUp()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                _pressLoadHolding = false;
                _pressStopping = false;

                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                await WriteCoilByKeyAsync("Cmd_PressLoadSetTrigger", false).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// 卸载（按住）：下压电机反向点动保持；到零位限位触发后自动停机并提示。
        /// </summary>
        public async void PressUnloadDown()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                SetMode(TrialMode.LoadSystem);

                await ApplyJogSetupFromReferenceAsync().ConfigureAwait(false);

                // 若已在零位，直接提示即可
                if (_pressLimit)
                {
                    UiMessageRequested?.Invoke("加载系统", "下压电机已在零位（限位已触发）。");
                    return;
                }

                if (!_daqService.IsRunning || !_strainService.IsRunning)
                {
                    try { StartAcquisition(); }
                    catch (Exception ex) { SetAlarm($"Alarm: StartAcq failed: {ex.Message}"); }
                }

                await ApplyModeAndParametersAsync().ConfigureAwait(false);

                _pressStopping = false;
                _pressLoadHolding = false;
                _pressUnloadHolding = true;

                // 互斥：先确保正向释放 & 关闭设定载荷触发
                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                await WriteCoilByKeyAsync("Cmd_PressLoadSetTrigger", false).ConfigureAwait(false);

                // 反向点动（按住）
                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UiMessageRequested?.Invoke("加载系统", $"卸载（按住）下发失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 卸载松开：释放反向点动保持。
        /// </summary>
        public async void PressUnloadUp()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                _pressUnloadHolding = false;
                _pressStopping = false;

                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// UI 定时调用：实现“双保险”自动停机与提示
        /// </summary>
        private void CheckLoadSystemStopConditions()
        {
            if (_mode != TrialMode.LoadSystem) return;
            if (!_plcConnected) return;
            if (_pressStopping) return;

            // 1) 加载：载荷到设定值 或 下压限位触发
            if (_pressLoadHolding)
            {
                if (!_pressLimit) _pressLimitSeenFalseDuringLoad = true;

                bool reachedLoad = !double.IsNaN(_loadN) && _loadN >= _loadNSet;
                bool limitHit = _pressLimit && (_pressLimitSeenFalseDuringLoad || !_pressLimitAtStartLoad);

                if (reachedLoad || limitHit)
                {
                    _pressStopping = true;
                    _pressLoadHolding = false;

                    // 仅当“载荷到设定值”达标时，锁存该设定载荷，用于后续运动模式参数区显示。
                    if (reachedLoad)
                    {
                        _manualAppliedLoadValid = true;
                        CommandManager.InvalidateRequerySuggested();
                        _manualAppliedLoadN = _loadNSet;
                        OnPropertyChanged(nameof(ManualLoadSetForMotion));
                    }

                    string reason = reachedLoad
                        ? $"已加载到设定值 {_loadNSet:F1} N。"
                        : "触发电机限位，请复位并调整合适的载荷值！";

                    StopLoadSystemHoldWithMessageAsync(reason);
                }
            }

            // 2) 卸载：回到零位（限位触发）则停机并提示
            if (_pressUnloadHolding && _pressLimit)
            {
                _pressStopping = true;
                _pressUnloadHolding = false;

                // 卸载回零：清除“已加载到目标载荷”的锁存状态
                if (_manualAppliedLoadValid)
                {
                    _manualAppliedLoadValid = false;
                    CommandManager.InvalidateRequerySuggested();
                    OnPropertyChanged(nameof(ManualLoadSetForMotion));
                }

                StopLoadSystemHoldWithMessageAsync("下压电机回到零位（触发限位）。");
            }
        }

        /// <summary>
        /// 释放下压电机正/反向点动保持 + 关闭设定载荷触发，并弹出提示框。
        /// </summary>
        private async void StopLoadSystemHoldWithMessageAsync(string message)
        {
            try
            {
                // 尽力释放所有相关线圈
                if (EnsurePlcWriteReady())
                {
                    await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                    await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);
                    await WriteCoilByKeyAsync("Cmd_PressLoadSetTrigger", false).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
            finally
            {
                _pressStopping = false;
            }

            UiMessageRequested?.Invoke("加载系统", message);
        }

        /// <summary>
        /// 离开“加载系统”模式/急停时调用：静默释放相关线圈（不弹窗）
        /// </summary>
        private async void StopLoadSystemOutputsSilently()
        {
            if (!EnsurePlcWriteReady()) return;
            try
            {
                CancelPressOperationSilently();
                _pressStartHolding = false;
                await WriteCommandCompatByKeyAsync("Cmd_PressStart", false).ConfigureAwait(false);

                await WriteCommandCompatByKeyAsync("Cmd_PressJogForwardHold", false).ConfigureAwait(false);
                await WriteCommandCompatByKeyAsync("Cmd_PressJogReverseHold", false).ConfigureAwait(false);
                await WriteCoilByKeyAsync("Cmd_PressLoadSetTrigger", false).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }


        /// <summary>
        /// EStop：上位机立即进入急停状态；并尽力发总停止 + 去使能
        /// </summary>
        private async void EStop()
        {
            _pressLoadHolding = false;
            StopAuto2Run("estop");
            _pressUnloadHolding = false;
            _pressStopping = false;
            StopLoadSystemOutputsSilently();

            StopDaqStreaming();
            SetRunState(RunState.EStop);
            SetAlarm("Alarm: E-STOP");

            if (!EnsurePlcWriteReady()) return;

            try { await PulseCommandCompatByKeyAsync("Cmd_TotalStop", CommandPulseMs).ConfigureAwait(false); } catch { /* ignore */ }
            try { await WriteCommandCompatByKeyAsync("Cmd_BallDriveEnable", false).ConfigureAwait(false); } catch { /* ignore */ }
            try { await WriteCommandCompatByKeyAsync("Cmd_DiskDriveEnable", false).ConfigureAwait(false); } catch { /* ignore */ }
            try { await WriteCommandCompatByKeyAsync("Cmd_PressDriveEnable", false).ConfigureAwait(false); } catch { /* ignore */ }
        }

        private async void ResetAlarm()
        {
            _localAlarmText = "Alarm: None";
            _alarmDetailText = "";
            OnPropertyChanged(nameof(AlarmText));
            OnPropertyChanged(nameof(AlarmDetailText));

            if (!EnsurePlcWriteReady())
            {
                if (_runState == RunState.EStop || _runState == RunState.Fault)
                    SetRunState(_plcConnected ? RunState.Connected : RunState.Idle);

                NotifyStatus();
                return;
            }
            try
            {
                await PulseCommandCompatByKeyAsync("Cmd_TotalReset", CommandPulseMs).ConfigureAwait(false);

                if (_runState == RunState.EStop || _runState == RunState.Fault)
                    SetRunState(_plcConnected ? RunState.Connected : RunState.Idle);

                NotifyStatus();
            }
            catch (Exception ex)
            {
                SetAlarm("Alarm: Reset Failed - " + ex.Message);
            }
        }

        /// <summary>
        /// 设置报警文本并切换到 Fault 状态（非急停）
        /// </summary>
        private void SetAlarm(string text)
        {
            _localAlarmText = text;
            // 本地报警通常是“上位机侧异常/提示”，详情不叠加 PLC 详情
            _alarmDetailText = "";
            OnPropertyChanged(nameof(AlarmText));
            OnPropertyChanged(nameof(AlarmDetailText));
            if (_runState != RunState.EStop)
                SetRunState(RunState.Fault);
        }
        #endregion

        #region ========== 导出区（CSV） ==========
        /// <summary>
        /// 导出 CSV：点击按钮后弹出保存对话框，由用户自定义导出地址
        /// </summary>
        private void ExportCsv()
        {
            try
            {
                var defaultDir = Path.Combine(AppContext.BaseDirectory, "TribologyExports");
                Directory.CreateDirectory(defaultDir);

                var src = _recordingPath;
                if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
                {
                    UiMessageRequested?.Invoke("导出 CSV", "当前没有可导出的全量记录文件。请先点击【采集开始】并产生数据。\n\n说明：绘图缓存点数上限仅影响曲线显示窗口，不影响全量记录；全量记录文件会在采集开始时自动创建并边采集边写入。");
                    return;
                }

                // 请求 recorder 尽快 flush（后台线程仍会每秒自动 flush 一次）
                System.Threading.Interlocked.Exchange(ref _recordFlushRequest, 1);

                var dlg = new SaveFileDialog
                {
                    Title = "导出 CSV（全量记录）",
                    Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                    DefaultExt = ".csv",
                    AddExtension = true,
                    OverwritePrompt = true,
                    InitialDirectory = defaultDir,
                    FileName = Path.GetFileName(src),
                };

                if (dlg.ShowDialog() != true)
                    return;

                var dst = dlg.FileName;
                var srcPath = src;

                // 在后台线程完成复制，避免 UI 卡顿
                Task.Run(() =>
                {
                    try
                    {
                        // 给后台 recorder 一个极短的 flush 窗口（非严格同步，主要避免最后一两行缺失）
                        System.Threading.Thread.Sleep(200);

                        File.Copy(srcPath, dst, overwrite: true);

                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _localAlarmText = "Alarm: None";
                            _alarmDetailText = $"Exported: {Path.GetFileName(dst)}";
                            OnPropertyChanged(nameof(AlarmText));
                            OnPropertyChanged(nameof(AlarmDetailText));
                        }), DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _localAlarmText = "Alarm: Export Failed";
                            _alarmDetailText = ex.Message;
                            OnPropertyChanged(nameof(AlarmText));
                            OnPropertyChanged(nameof(AlarmDetailText));
                        }), DispatcherPriority.Background);
                    }
                });
            }
            catch (Exception ex)
            {
                _localAlarmText = "Alarm: Export Failed";
                _alarmDetailText = ex.Message;
                OnPropertyChanged(nameof(AlarmText));
                OnPropertyChanged(nameof(AlarmDetailText));
            }
        }

/// <summary>
/// 自动程序2导出：生成两份 CSV
/// - *_raw.csv：全采样原始序列（每次 UI Tick 一条）
/// - *_summary.csv：每个点位的平均值/有效时间（由 SingleDone 脉冲划分）
/// </summary>
private void ExportAuto2Csv()
{
    try
    {
        var defaultDir = Path.Combine(AppContext.BaseDirectory, "TribologyExports");
        Directory.CreateDirectory(defaultDir);

        if (_auto2Raw.Count == 0 && _auto2Summary.Count == 0)
        {
            UiMessageRequested?.Invoke("Auto2 导出", "当前没有自动程序2的数据可导出。请先运行自动程序2并产生 SingleDone 点位数据。");
            return;
        }

        var baseName = $"auto2_{DateTime.Now:yyyyMMdd_HHmmss}";
        var dlg = new SaveFileDialog
        {
            Title = "导出 Auto2 CSV（将同时生成 raw/summary 两个文件）",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = defaultDir,
            FileName = baseName + "_raw.csv",
        };

        if (dlg.ShowDialog() != true)
            return;

        string rawPath = dlg.FileName;
        string dir = Path.GetDirectoryName(rawPath) ?? defaultDir;

        // 规范化：用选择的 raw 文件名推导 summary 文件名
        string rawName = Path.GetFileNameWithoutExtension(rawPath);
        string prefix = rawName.EndsWith("_raw", StringComparison.OrdinalIgnoreCase)
            ? rawName.Substring(0, rawName.Length - 4)
            : rawName;

        string summaryPath = Path.Combine(dir, prefix + "_summary.csv");

        static string F(double v, string fmt)
            => (double.IsNaN(v) || double.IsInfinity(v)) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);

        // raw
        var sbRaw = new StringBuilder();
        sbRaw.AppendLine("t_s,point,traction_force_N,traction_coeff,load_raw,load_N,ball_mmps,disk_mmps,reel_mmps,srr,temperature_C");
        foreach (var s in _auto2Raw)
        {
            sbRaw.AppendLine(
                $"{F(s.TimeSeconds, "F4")}," +
                $"{s.PointIndex}," +
                $"{F(s.TractionForceN, "F5")}," +
                $"{F(s.Mu, "F6")}," +
                $"{F(s.SerialLoadRaw, "F3")}," +
                $"{F(s.LoadN, "F3")}," +
                $"{F(s.BallSpeedMmps, "F3")}," +
                $"{F(s.DiskSpeedMmps, "F3")}," +
                $"{F(s.RollingSpeedMmps, "F3")}," +
                $"{F(s.Srr, "F6")}," +
                $"{F(s.TemperatureC, "F2")}");
        }
        File.WriteAllText(rawPath, sbRaw.ToString(), Encoding.UTF8);

        // summary
        var sbSum = new StringBuilder();
        sbSum.AppendLine("point,target_disk_mmps,target_ball_mmps,target_srr,stage_time_s,start_s,end_s,effective_s,avg_force_N,avg_mu,avg_load_N,avg_ball_mmps,avg_disk_mmps");
        foreach (var s in _auto2Summary)
        {
            sbSum.AppendLine(
                $"{s.PointIndex}," +
                $"{F(s.TargetDiskMmps, "F3")}," +
                $"{F(s.TargetBallMmps, "F3")}," +
                $"{F(s.TargetSrr, "F6")}," +
                $"{F(s.StageTimeSeconds, "F2")}," +
                $"{F(s.StartSeconds, "F4")}," +
                $"{F(s.EndSeconds, "F4")}," +
                $"{F(s.EffectiveSeconds, "F3")}," +
                $"{F(s.AvgTractionForceN, "F5")}," +
                $"{F(s.AvgMu, "F6")}," +
                $"{F(s.AvgLoadN, "F3")}," +
                $"{F(s.AvgBallSpeedMmps, "F3")}," +
                $"{F(s.AvgDiskSpeedMmps, "F3")}");
        }
        File.WriteAllText(summaryPath, sbSum.ToString(), Encoding.UTF8);

        _localAlarmText = $"Alarm: None (Auto2 Exported: {Path.GetFileName(rawPath)}, {Path.GetFileName(summaryPath)})";
        _alarmDetailText = "";
        OnPropertyChanged(nameof(AlarmText));
        OnPropertyChanged(nameof(AlarmDetailText));
    }
    catch (Exception ex)
    {
        _localAlarmText = "Alarm: Auto2 Export Failed - " + ex.Message;
        _alarmDetailText = "";
        OnPropertyChanged(nameof(AlarmText));
        OnPropertyChanged(nameof(AlarmDetailText));
    }
}

#endregion

        #region ========== 相机区（USB/网口，eBUS PvDotNet） ==========
        private void CameraConnect()
        {
            try
            {
                var dlg = new Lanzhou_v1._0.Camera.CameraSelectWindow
                {
                    Owner = System.Windows.Application.Current?.MainWindow
                };

                var ok = dlg.ShowDialog();
                if (ok != true || dlg.Result == null) return;

                // 先断开旧连接（若存在）
                if (_cameraService.IsConnected)
                    _cameraService.Disconnect();

                _cameraService.Connect(dlg.Result);

                _cameraStatusText = _cameraService.StatusText;
                OnPropertyChanged(nameof(CameraStatusText));
                NotifyStatus();
                RefreshCommandState();
            }
            catch (Exception ex)
            {
                _cameraStatusText = "相机连接失败：" + ex.Message;
                OnPropertyChanged(nameof(CameraStatusText));
                NotifyStatus();
                RefreshCommandState();
                UiMessageRequested?.Invoke("相机", _cameraStatusText);
            }
        }

        private void CameraDisconnect()
        {
            try
            {
                _cameraService.Disconnect();
            }
            catch { /* ignore */ }

            _cameraBitmap = null;
            _cameraStatusText = "相机未连接";
            OnPropertyChanged(nameof(CameraPreviewImage));
            OnPropertyChanged(nameof(CameraStatusText));
            NotifyStatus();
            RefreshCommandState();
        }

        private void OnCameraFrameArrived(CameraFrame frame)
        {
            // 相机线程 -> UI 线程更新 WriteableBitmap
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return;

            disp.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_cameraBitmap == null || _cameraBitmap.PixelWidth != frame.Width || _cameraBitmap.PixelHeight != frame.Height || _cameraBitmap.Format != frame.PixelFormat)
                    {
                        _cameraBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, frame.PixelFormat, null);
                        OnPropertyChanged(nameof(CameraPreviewImage));
                    }

                    _cameraBitmap.WritePixels(
                        new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height),
                        frame.Pixels,
                        frame.Stride,
                        0);
                }
                catch
                {
                    // ignore preview update failures
                }
            }));
        }

        private void CameraCapture(bool isAuto)
        {
            if (!_cameraService.IsConnected)
            {
                UiMessageRequested?.Invoke("相机", "相机未连接。请先点击【连接并选择相机】。");
                return;
            }

            if (!_cameraService.TryGetLatestFrame(out var frame) || frame == null)
            {
                UiMessageRequested?.Invoke("相机", "尚未收到图像帧。请稍等 1~2 秒再拍照。\n（若持续无图，请检查相机供电/连接与 eBUS 运行环境）");
                return;
            }
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "TribologyPhotos");
                Directory.CreateDirectory(dir);

                var prefix = isAuto ? "auto1" : "manual";
                var file = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                var path = Path.Combine(dir, file);

                SaveFrameAsJpeg(frame, path);

                _lastPhotoPath = path;
                OnPropertyChanged(nameof(LastPhotoPath));

                if (!isAuto)
                    UiMessageRequested?.Invoke("相机", $"拍照完成，已保存：\n{path}");
            }
            catch (Exception ex)
            {
                UiMessageRequested?.Invoke("相机", "拍照保存失败：" + ex.Message);
            }
        }

        private static void SaveFrameAsJpeg(CameraFrame frame, string path)
        {
            // WPF 编码器：无需 System.Drawing
            var bs = BitmapSource.Create(
                frame.Width, frame.Height,
                96, 96,
                frame.PixelFormat,
                null,
                frame.Pixels,
                frame.Stride);
            bs.Freeze();

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(bs));
            enc.Save(fs);
        }

        private void OpenPhotoFolder()
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "TribologyPhotos");
                Directory.CreateDirectory(dir);

                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                UiMessageRequested?.Invoke("相机", "打开照片目录失败：" + ex.Message);
            }
        }

        /// <summary>
        /// Auto1 自动拍照（PLC 握手版）：
        /// - PLC 执行到拍照点：St_PhotoRequest(4x407) = 1
        /// - HMI 读到请求后：拍照并保存 -> Cmd_Auto1_CameraDone(0x319) = 1
        /// - PLC 读到 Done 后：清零 St_PhotoRequest，并继续流程
        /// - HMI 看到请求清零后：将 Done 复位为 0（一次握手完成）
        ///
        /// 设计要点：
        /// - 使用“Done 保持到请求清零”的方式，避免 PLC 因扫描时序错过短脉冲。
        /// - 拍照失败不回写 Done，并按 Auto1PhotoRetryIntervalMs 节流，防止轮询频繁重试。
        /// </summary>
        private void UpdateAuto1PhotoState(double tNowSeconds)
        {
            try
            {
                bool inWork = (_mode == TrialMode.AutoProgram1 && _runState == RunState.Running);
                bool reqActive = _plcPhotoRequestRaw != 0;

                // 非工作态：尽量复位（若曾置 Done=1，则尝试清零）
                if (!inWork)
                {
                    if (_auto1CameraDoneHolding)
                    {
                        _auto1CameraDoneHolding = false;
                        if (_plcConnected && _pointMap != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await WriteCommandCompatByKeyAsync("Cmd_Auto1_CameraDone", false).ConfigureAwait(false); }
                                catch { /* ignore */ }
                            });
                        }
                    }
                    _auto1PhotoBusy = false;
                    if (_auto1AutoPhotoStatusText != "Auto1 拍照：未触发")
                    {
                        _auto1AutoPhotoStatusText = "Auto1 拍照：未触发";
                        OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                    }
                    return;
                }

                if (!_auto1AutoPhotoEnabled)
                {
                    // 用户关闭：不处理请求；但若 Done 正在保持，则清零以免 PLC 卡死
                    if (_auto1CameraDoneHolding)
                    {
                        _auto1CameraDoneHolding = false;
                        if (_plcConnected && _pointMap != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await WriteCommandCompatByKeyAsync("Cmd_Auto1_CameraDone", false).ConfigureAwait(false); }
                                catch { /* ignore */ }
                            });
                        }
                    }

                    if (_auto1AutoPhotoStatusText != "Auto1 拍照：已关闭")
                    {
                        _auto1AutoPhotoStatusText = "Auto1 拍照：已关闭";
                        OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                    }
                    return;
                }

                // 1) 若 Done 已置 1：等待 PLC 清零请求后再复位 Done
                if (_auto1CameraDoneHolding)
                {
                    if (!reqActive)
                    {
                        _auto1CameraDoneHolding = false;

                        if (_plcConnected && _pointMap != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await WriteCommandCompatByKeyAsync("Cmd_Auto1_CameraDone", false).ConfigureAwait(false); }
                                catch { /* ignore */ }
                            });
                        }

                        if (_auto1AutoPhotoStatusText != "Auto1 拍照：握手完成")
                        {
                            _auto1AutoPhotoStatusText = "Auto1 拍照：握手完成";
                            OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                        }
                    }
                    else
                    {
                        const string s = "Auto1 拍照：Done=1，等待 PLC 清零请求...";
                        if (_auto1AutoPhotoStatusText != s)
                        {
                            _auto1AutoPhotoStatusText = s;
                            OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                        }
                    }
                    return;
                }

                // 2) 未请求：空闲
                if (!reqActive)
                {
                    const string s = "Auto1 拍照：等待 PLC 请求...";
                    if (_auto1AutoPhotoStatusText != s)
                    {
                        _auto1AutoPhotoStatusText = s;
                        OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                    }
                    return;
                }

                // 3) 请求=1：防重入/节流
                if (_auto1PhotoBusy)
                {
                    const string s = "Auto1 拍照：处理中...";
                    if (_auto1AutoPhotoStatusText != s)
                    {
                        _auto1AutoPhotoStatusText = s;
                        OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                    }
                    return;
                }

                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - _auto1LastPhotoAttemptUtc).TotalMilliseconds < Auto1PhotoRetryIntervalMs)
                    return;

                // 写前置检查（在 UI 线程做，避免后台线程触发 WPF 绑定跨线程更新）
                if (!_plcConnected || _pointMap == null)
                {
                    if (_auto1AutoPhotoStatusText != "Auto1 拍照：PLC 未连接/点表未加载")
                    {
                        _auto1AutoPhotoStatusText = "Auto1 拍照：PLC 未连接/点表未加载";
                        OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                    }
                    return;
                }

                _auto1LastPhotoAttemptUtc = nowUtc;
                _auto1PhotoBusy = true;
                _auto1AutoPhotoStatusText = "Auto1 拍照：收到 PLC 请求，拍照中...";
                OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));

                Task.Run(async () =>
                {
                    try
                    {
                        if (!_cameraService.IsConnected)
                            throw new InvalidOperationException("相机未连接（请先选择并连接相机）");

                        if (!_cameraService.TryGetLatestFrame(out var fr) || fr == null)
                            throw new InvalidOperationException("尚未收到图像帧（请等待 1~2 秒或检查相机取流）");

                        // 保存照片
                        var dir = Path.Combine(AppContext.BaseDirectory, "TribologyPhotos");
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, $"auto1_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");
                        SaveFrameAsJpeg(fr, path);

                        // 回写 Done=1，并保持到 PLC 清零请求
                        await WriteCommandCompatByKeyAsync("Cmd_Auto1_CameraDone", true).ConfigureAwait(false);

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            _lastPhotoPath = path;
                            OnPropertyChanged(nameof(LastPhotoPath));

                            _auto1CameraDoneHolding = true;
                            _auto1AutoPhotoStatusText = "Auto1 拍照：Done=1，等待 PLC 清零请求...";
                            OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            _auto1AutoPhotoStatusText = "Auto1 拍照：失败 - " + ex.Message;
                            OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
                        });
                    }
                    finally
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() => { _auto1PhotoBusy = false; });
                    }
                });
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            try { SaveUserSettingsNow(); } catch { }
            try { _cameraService.FrameArrived -= OnCameraFrameArrived; } catch { }
            try { _cameraService.Dispose(); } catch { }
        }
        #endregion

        #region ========== 状态通知与命令刷新区 ==========
        /// <summary>
        /// 一次性通知状态栏相关绑定刷新
        /// </summary>
        private void NotifyStatus()
        {
            OnPropertyChanged(nameof(PlcStatusText));
            OnPropertyChanged(nameof(DaqStatusText));
            OnPropertyChanged(nameof(SerialStatusText));
            OnPropertyChanged(nameof(CameraStatusText));
            OnPropertyChanged(nameof(LastPhotoPath));
            OnPropertyChanged(nameof(Auto1AutoPhotoStatusText));
            OnPropertyChanged(nameof(CurrentModeText));
            OnPropertyChanged(nameof(RunStateText));
            OnPropertyChanged(nameof(AlarmText));
            OnPropertyChanged(nameof(AlarmDetailText));
            OnPropertyChanged(nameof(PlcProbeText));
            OnPropertyChanged(nameof(SampleCountText));
        }

        /// <summary>
        /// 刷新 WPF 命令可用性（触发 CanExecute 重新评估）
        /// </summary>
        private void RefreshCommandState()
        {
            CommandManager.InvalidateRequerySuggested();
        }
        #endregion
    }

    #region ========== RelayCommand（命令封装，WPF 常用） ==========
    /// <summary>
    /// 简单 ICommand 实现：将按钮点击映射到 Action，并支持 CanExecute。
    /// 维护者提示：如后续需要异步命令/防重入，可替换为 AsyncRelayCommand。
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
    #endregion
}
