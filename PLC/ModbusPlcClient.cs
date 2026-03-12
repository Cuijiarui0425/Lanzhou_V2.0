using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Lanzhou_v1._0.PLC
{
    #region ========== MVP 快照数据结构（供 ViewModel 轮询显示） ==========
    /// <summary>
    /// PlcProbeSnapshot：一次轮询读回的“快照”
    /// 目前用于：
    /// - PlcProbeText 显示（联调可视化）
    /// - 可选：UseSimulatedSource=false 时用 snap.Speed/snap.Load 刷新右侧数值
    ///
    /// 该结构的字段对应 pointmap_mvp.csv 里的 1x/3x 点位：
    /// - 1x: St_Ready / St_Running / St_AlarmActive
    /// - 3x: StateCode / AlarmCode / Speed / Load / Temp / Time（Float32）
    /// </summary>
    public sealed class PlcProbeSnapshot
    {
        // PLC→HMI（状态）
        public bool BallEnableDone { get; set; }
        public bool DiskEnableDone { get; set; }
        public bool PressEnableDone { get; set; }
        public bool PressLimit { get; set; }
        public bool MotionDone { get; set; }
        public bool MotionFail { get; set; }

        // PLC→HMI（状态）
        public bool Auto2SingleDone { get; set; }

        /// <summary>
        /// PLC→HMI：拍照请求（交互表：St_PhotoRequest / 4x 407 / UInt16）
        /// - 现场验证：Modbus FC03 读取 4x 地址 407 可得到 1/0
        /// - 使用 Raw 口径：非 0 即认为请求有效
        /// </summary>
        public ushort PhotoRequestRaw { get; set; }

        public short ErrorCode { get; set; }

        // PLC→HMI（测量值）
        public float BallSpeed { get; set; }
        public float DiskSpeed { get; set; }
        public float PressSpeed { get; set; }

        public float BallTorque { get; set; }
        public float DiskTorque { get; set; }
        public float PressTorque { get; set; }

        // 报警代码在最终交互表中为 DWord（UInt32，占用 2 个寄存器）
        public uint BallAlarmCode { get; set; }
        public uint DiskAlarmCode { get; set; }
        public uint PressAlarmCode { get; set; }

        // PLC→HMI（阈值/限制）
        public float LoadAlarmLimit { get; set; }
    }
    #endregion

    /// <summary>
    /// ModbusPlcClient：纯 .NET 8 的最小 Modbus TCP Client（无第三方库）
    ///
    /// 目的：
    /// - 规避第三方库与 net8 的兼容问题（如 NModbus4 的 NU1701）
    /// - 仅实现项目所需的“窄功能集”，便于验收与维护
    ///
    /// 协议要点（给甲方维护者）：
    /// - Modbus TCP = MBAP(7字节) + PDU(功能码+数据)
    /// - MBAP = TransactionId(2) + ProtocolId(2=0) + Length(2) + UnitId(1)
    /// - PDU  = FunctionCode(1) + Data(N)
    ///
    /// 地址口径（非常关键）：
    /// - 本项目默认采用 Base0（你已在 Modbus Slave 勾选 Protocol Addresses (Base 0) 验证）
    /// - 若真 PLC 文档采用 Base1，则点表 Address 需要整体 ±1（或在客户端层统一换算）
    ///
    /// 已实现功能码：
    /// - FC01 读 0x (Coils)                  [可选]
    /// - FC02 读 1x (Discrete Inputs)        [用于状态位]
    /// - FC03 读 4x (Holding Registers)      [可选]
    /// - FC04 读 3x (Input Registers)        [用于测量/状态码]
    /// - FC05 写 0x (Write Single Coil)      [用于命令位]
    /// - FC06 写 4x (Write Single Register)  [可选]
    /// - FC10 写 4x (Write Multiple Registers)[用于 float32/参数]
    ///
    /// Float32 约定：
    /// - 默认使用 AB CD（高字在前、低字在后）的 2 寄存器方式
    /// - 如果真 PLC 使用不同字序，需在 ToFloat/FromFloat 中调整（或引入 Tag 的 ByteOrder 字段）
    /// </summary>
    public sealed class ModbusPlcClient : IDisposable
    {
        #region ========== 连接对象与并发控制区（关键） ==========
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;

        /// <summary>
        /// I/O 锁：避免多个异步请求同时读写同一个 NetworkStream 导致帧错乱
        /// 说明：Modbus TCP 是“请求-响应”模型，通常应串行请求。
        /// </summary>
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// TransactionId（事务号）：每次请求自增，用于匹配响应
        /// 某些设备不严格回显 TID，但本实现默认严格检查，便于联调排障。
        /// </summary>
        private ushort _transactionId = 0;

        public bool IsConnected { get; private set; }

        /// <summary>
        /// UnitId（站号/从站地址）：Modbus TCP 中仍保留该字段，通常为 1。
        /// 对于网关或多从站场景非常关键。
        /// </summary>
        public byte UnitId { get; private set; } = 1;

        /// <summary>
        /// IO 超时（ms）：读/写网络超时
        /// - 联调初期建议 500~1000ms，便于快速发现“无响应/断连/端口占用”
        /// - 现场稳定后可适度增大（避免偶发网络抖动导致误报）
        /// </summary>
        public int IoTimeoutMs { get; set; } = 800;
        #endregion

        #region ========== 连接管理区 ==========
        /// <summary>
        /// ConnectAsync：建立 Modbus TCP 连接
        /// </summary>
        public async Task ConnectAsync(string ip, int port, byte unitId)
        {
            Disconnect();

            _tcpClient = new TcpClient();
            _tcpClient.NoDelay = true; // 减少小包延迟
            await _tcpClient.ConnectAsync(ip, port).ConfigureAwait(false);

            _stream = _tcpClient.GetStream();

            UnitId = unitId;
            IsConnected = true;
        }

        /// <summary>
        /// Disconnect：断开连接并释放资源
        /// </summary>
        public void Disconnect()
        {
            IsConnected = false;

            try { _stream?.Close(); } catch { /* ignore */ }
            try { _tcpClient?.Close(); } catch { /* ignore */ }

            _stream = null;
            _tcpClient = null;
        }

        public void Dispose() => Disconnect();
        #endregion

        #region ========== 业务级读接口（MVP 快照） ==========
        /// <summary>
        /// ReadMvpSnapshotAsync：读取 MVP 所需的“最小快照”
        ///
        /// 点位布局（由 pointmap_mvp.csv 与你当前约定决定）：
        /// - 1x: 0..2 (Ready/Running/Alarm)
        /// - 3x: 10..27
        ///   - 10: StateCode
        ///   - 11: AlarmCode
        ///   - 20..21: Speed (Float32)
        ///   - 22..23: Load  (Float32)
        ///   - 24..25: Temp  (Float32)
        ///   - 26..27: Time  (Float32)
        ///
        /// 说明：
        /// - 为简化联调，本实现一次读一段连续寄存器（10..27），降低请求次数。
        /// - 若真点表分散，可改为多段读或做“分段合并”策略（后续可扩展）。
        /// </summary>
        public async Task<PlcProbeSnapshot> ReadMvpSnapshotAsync()
        {
            EnsureConnected();

            // 按电机公司提供的“PLC→HMI”交互表读取：
            // - 状态位：MX800.* 对应 TCP 地址 400..405（本项目已验证映射到 0x/Coils，使用 FC01 读取）
            // - 错误码：MW800 对应 TCP 地址 800（1 个寄存器）
            // - 实时量：MD800..MD808 对应 TCP 地址 1600..1616（Float32，共 9 个量）
            // 1) 读状态位（400..405 共 6 位）
            // 说明：不同 PLC/网关实现可能将“状态位”映射到：
            // - 0x(Coils, FC01)
            // - 1x(Discrete Inputs, FC02)
            // 甚至同一台设备在不同版本程序中会切换映射口径。
            //
            // 为提高现场兼容性：同时尝试 FC01 与 FC02，并按“有效位数量”选择更可信的一组。
            bool[] diCoils = Array.Empty<bool>();
            bool[] diInputs = Array.Empty<bool>();

            try { diCoils = await ReadCoilsAsync(400, 6).ConfigureAwait(false); }
            catch { /* ignore */ }

            try { diInputs = await ReadDiscreteInputsAsync(400, 6).ConfigureAwait(false); }
            catch { /* ignore */ }

            int Score(bool[] bits)
            {
                if (bits == null) return 0;
                int c = 0;
                for (int i = 0; i < bits.Length; i++) if (bits[i]) c++;
                return c;
            }

            int scoreCoils = Score(diCoils);
            int scoreInputs = Score(diInputs);
            bool[] di = (scoreInputs > scoreCoils) ? diInputs : diCoils;

            if (di == null || di.Length == 0)
                di = new bool[6];

            // 1.1) 运动完成/失败（MotionDone/MotionFail）兼容处理
            // 现场已出现两种映射口径：
            // - 0x/1x 位（FC01/FC02）：MX808.* → TCP 400..405
            // - 4x 保持寄存器（FC03）：4x404/4x405 → UInt16（0/非0）
            // 本项目优先使用位读（di[4]/di[5]），并在读到 4x 时覆盖，以兼容 404/405 在 4x 的现场。
            bool motionDone = di.Length > 4 && di[4];
            bool motionFail = di.Length > 5 && di[5];

            try
            {
                ushort[] mf;
                try { mf = await ReadHoldingRegistersAsync(404, 2).ConfigureAwait(false); }
                catch { mf = await ReadInputRegistersAsync(404, 2).ConfigureAwait(false); } // 极少数现场可能映射到 3x

                if (mf != null && mf.Length >= 1) motionDone = mf[0] != 0;
                if (mf != null && mf.Length >= 2) motionFail = mf[1] != 0;
            }
            catch
            {
                // 忽略：保持位读结果
            }

            // 2) 读 Auto2 单次完成（St_Auto2_SingleDone）
            // 点表口径在现场可能是 0x/1x 位，也可能是 4x/3x 的 UInt16（保持位）。
            // 约定：0=未完成，非0=完成（PLC 置 1 并保持，直到收到 Cmd_Auto2_DataDone(322) 后清 0）
            bool auto2SingleDone = false;
            try
            {
                ushort[] reg;
                try { reg = await ReadHoldingRegistersAsync(406, 1).ConfigureAwait(false); }
                catch { reg = await ReadInputRegistersAsync(406, 1).ConfigureAwait(false); }
                auto2SingleDone = reg != null && reg.Length > 0 && reg[0] != 0;
            }
            catch
            {
                // fallback: bit read
                try
                {
                    bool[] b;
                    try { b = await ReadCoilsAsync(406, 1).ConfigureAwait(false); }
                    catch { b = await ReadDiscreteInputsAsync(406, 1).ConfigureAwait(false); }
                    auto2SingleDone = b != null && b.Length > 0 && b[0];
                }
                catch
                {
                    auto2SingleDone = false;
                }
            }

            // 3) 读拍照请求（St_PhotoRequest -> 4x 407 / UInt16）
            // 说明：该点位与 800/1600 不连续，因此单独读 1 个寄存器。
            ushort photoReq = 0;
            try
            {
                var pr = await ReadHoldingRegistersAsync(407, 1).ConfigureAwait(false);
                photoReq = pr != null && pr.Length > 0 ? pr[0] : (ushort)0;
            }
            catch
            {
                // 少数现场将其映射到 3x：容错
                try
                {
                    var pr = await ReadInputRegistersAsync(407, 1).ConfigureAwait(false);
                    photoReq = pr != null && pr.Length > 0 ? pr[0] : (ushort)0;
                }
                catch
                {
                    photoReq = 0;
                }
            }

            // 4) 读错误码（MW800 -> TCP 800）
            // 交互表仅给出“TCP 地址”，不强制 3x/4x 口径。现场常见两种映射：
            // - 4x Holding Register（FC03）
            // - 3x Input Register  （FC04）
            // 为提高兼容性：优先读 4x，失败则回退读 3x。
            ushort[] err;
            try { err = await ReadHoldingRegistersAsync(800, 1).ConfigureAwait(false); }
            catch { err = await ReadInputRegistersAsync(800, 1).ConfigureAwait(false); }
            short errorCode = unchecked((short)err[0]);

            // 3) 读实时量（MD800..MD808 -> TCP 1600..1617，共 18 个寄存器 = 9 个 Float32）
            // 同样采用“优先 4x，失败回退 3x”的兼容策略。
            ushort[] ir;
            try { ir = await ReadHoldingRegistersAsync(1600, 20).ConfigureAwait(false); }
            catch { ir = await ReadInputRegistersAsync(1600, 20).ConfigureAwait(false); }

            float f(int startIndex)
            {
                // startIndex: 0,2,6,8,12,14
                return ToFloat_ABCD(ir[startIndex], ir[startIndex + 1]);
            }

            uint u(int startIndex)
            {
                // startIndex: 4,10,16 (DWord)
                return ToUInt32_ABCD(ir[startIndex], ir[startIndex + 1]);
            }

            var snap = new PlcProbeSnapshot
            {
                BallEnableDone = di.Length > 0 && di[0],
                DiskEnableDone = di.Length > 1 && di[1],
                PressEnableDone = di.Length > 2 && di[2],
                PressLimit = di.Length > 3 && di[3],
                MotionDone = motionDone,
                MotionFail = motionFail,
                Auto2SingleDone = auto2SingleDone,

                PhotoRequestRaw = photoReq,

                ErrorCode = errorCode,

                BallSpeed = f(0),
                BallTorque = f(2),
                BallAlarmCode = u(4),

                DiskSpeed = f(6),
                DiskTorque = f(8),
                DiskAlarmCode = u(10),

                PressSpeed = f(12),
                PressTorque = f(14),
                PressAlarmCode = u(16),

                LoadAlarmLimit = f(18),
            };

            return snap;
        }
        #endregion

        #region ========== 写接口区（0x 命令位 / 4x 参数） ==========
        /// <summary>
        /// WriteCoilAsync：写单线圈（FC05）
        /// - 常用于命令位（Cmd_Start/Cmd_Stop 等）
        /// - value=true -> 0xFF00；value=false -> 0x0000（Modbus 标准）
        /// </summary>
        public async Task WriteCoilAsync(ushort address, bool value)
        {
            EnsureConnected();

            byte[] req = new byte[4];
            WriteUInt16BE(req, 0, address);
            WriteUInt16BE(req, 2, value ? (ushort)0xFF00 : (ushort)0x0000);

            byte[] pdu = await SendRequestAsync(0x05, req).ConfigureAwait(false);

            // 正常响应：FC + addr(2) + value(2)
            if (pdu.Length < 5) throw new Exception("Modbus 响应长度异常（WriteSingleCoil）");
        }

        /// <summary>
        /// PulseCoilAsync：脉冲式命令位（置 1 -> 延时 -> 置 0）
        /// - PLC 侧常用上升沿触发
        /// - pulseMs 为脉冲宽度（可按 PLC 扫描周期调整）
        /// </summary>
        public async Task PulseCoilAsync(ushort address, int pulseMs = 150)
        {
            await WriteCoilAsync(address, true).ConfigureAwait(false);
            await Task.Delay(pulseMs).ConfigureAwait(false);
            await WriteCoilAsync(address, false).ConfigureAwait(false);
        }

        /// <summary>
        /// WriteSingleRegisterAsync：写单寄存器（FC06）
        /// 可用于简单 UInt16 参数写入（可选功能）
        /// </summary>
        public async Task WriteSingleRegisterAsync(ushort address, ushort value)
        {
            EnsureConnected();

            byte[] req = new byte[4];
            WriteUInt16BE(req, 0, address);
            WriteUInt16BE(req, 2, value);

            byte[] pdu = await SendRequestAsync(0x06, req).ConfigureAwait(false);
            if (pdu.Length < 5) throw new Exception("Modbus 响应长度异常（WriteSingleRegister）");
        }

        /// <summary>
        /// WriteUInt16HoldingRegisterAsync：兼容旧命名/上层调用。
        /// - 等价于 WriteSingleRegisterAsync(address, value)
        /// </summary>
        public Task WriteUInt16HoldingRegisterAsync(ushort address, ushort value)
        {
            return WriteSingleRegisterAsync(address, value);
        }

        /// <summary>
        /// WriteInt16HoldingRegisterAsync：写 Int16（按二进制补码写入单寄存器）。
        /// </summary>
        public Task WriteInt16HoldingRegisterAsync(ushort address, short value)
        {
            return WriteSingleRegisterAsync(address, unchecked((ushort)value));
        }

        /// <summary>
        /// WriteHoldingRegistersAsync：写多寄存器（FC16 / 0x10）
        /// - 用于 Float32 / Int32 / 多参数批量下发
        /// - values 数量通常建议 <= 123（多数 PLC 上限 125，留出帧结构开销）
        /// </summary>
        public async Task WriteHoldingRegistersAsync(ushort startAddress, ushort[] values)
        {
            EnsureConnected();
            if (values == null || values.Length == 0) throw new ArgumentException("values 不能为空", nameof(values));
            if (values.Length > 123) throw new ArgumentOutOfRangeException(nameof(values), "FC16 单次建议不超过 123 寄存器");

            ushort quantity = (ushort)values.Length;
            byte byteCount = (byte)(quantity * 2);

            // PDU Data：start(2) + qty(2) + byteCount(1) + data(2*qty)
            byte[] req = new byte[5 + byteCount];
            WriteUInt16BE(req, 0, startAddress);
            WriteUInt16BE(req, 2, quantity);
            req[4] = byteCount;

            int offset = 5;
            for (int i = 0; i < values.Length; i++)
            {
                WriteUInt16BE(req, offset + i * 2, values[i]);
            }

            byte[] pdu = await SendRequestAsync(0x10, req).ConfigureAwait(false);

            // 正常响应：FC + start(2) + qty(2)
            if (pdu.Length < 5) throw new Exception("Modbus 响应长度异常（WriteMultipleRegisters）");
        }

        /// <summary>
        /// WriteFloat32HoldingRegistersAsync：写 float32（占 2 个寄存器）
        /// 字序：低字在前（LowWord -> 高字在后 HighWord）
        /// - 与 MotorFinalTest_V3 以及现场验证一致：第 1 寄存器为低字，第 2 寄存器为高字
        /// </summary>
        public async Task WriteFloat32HoldingRegistersAsync(ushort startAddress, float value)
        {
            var (lo, hi) = FromFloat_ABCD(value);
            await WriteHoldingRegistersAsync(startAddress, new ushort[] { lo, hi }).ConfigureAwait(false);
        }

        /// <summary>
        /// WriteInt32HoldingRegistersAsync：写 Int32（占 2 个寄存器）
        /// 字序：低字在前（LowWord -> 高字在后 HighWord）
        /// - 与本项目 Float32 写入保持一致
        /// </summary>
        public async Task WriteInt32HoldingRegistersAsync(ushort startAddress, int value)
        {
            ushort lo = unchecked((ushort)(value & 0xFFFF));
            ushort hi = unchecked((ushort)((value >> 16) & 0xFFFF));
            await WriteHoldingRegistersAsync(startAddress, new ushort[] { lo, hi }).ConfigureAwait(false);
        }
#endregion

        #region ========== 读接口区（FC01/02/03/04） ==========
        /// <summary>
        /// ReadCoilsAsync：读线圈（FC01）
        /// - 用于读取 0x（可选）
        /// - 返回 bool[]（按请求 quantity）
        /// </summary>
        public async Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort quantity)
        {
            byte[] req = new byte[4];
            WriteUInt16BE(req, 0, startAddress);
            WriteUInt16BE(req, 2, quantity);

            byte[] pdu = await SendRequestAsync(0x01, req).ConfigureAwait(false);

            // 响应：FC(1) + byteCount(1) + data(N)
            if (pdu.Length < 2) throw new Exception("Modbus 响应长度异常（ReadCoils）");

            byte byteCount = pdu[1];
            if (pdu.Length < 2 + byteCount) throw new Exception("Modbus 响应长度不足（ReadCoils）");

            bool[] bits = new bool[quantity];
            for (int i = 0; i < quantity; i++)
            {
                int byteIndex = 2 + (i / 8);
                int bitIndex = i % 8;
                bits[i] = (pdu[byteIndex] & (1 << bitIndex)) != 0;
            }
            return bits;
        }

        /// <summary>
        /// ReadDiscreteInputsAsync：读离散输入（FC02）
        /// - 本项目 MVP 轮询状态位使用此函数
        /// </summary>
        private async Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort quantity)
        {
            byte[] req = new byte[4];
            WriteUInt16BE(req, 0, startAddress);
            WriteUInt16BE(req, 2, quantity);

            byte[] pdu = await SendRequestAsync(0x02, req).ConfigureAwait(false);

            if (pdu.Length < 2) throw new Exception("Modbus 响应长度异常（ReadDiscreteInputs）");

            byte byteCount = pdu[1];
            if (pdu.Length < 2 + byteCount) throw new Exception("Modbus 响应长度不足（ReadDiscreteInputs）");

            bool[] bits = new bool[quantity];
            for (int i = 0; i < quantity; i++)
            {
                int byteIndex = 2 + (i / 8);
                int bitIndex = i % 8;
                bits[i] = (pdu[byteIndex] & (1 << bitIndex)) != 0;
            }
            return bits;
        }

        /// <summary>
        /// ReadHoldingRegistersAsync：读保持寄存器（FC03）
        /// - 可用于读取 4x 参数（可选）
        /// </summary>
        public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort quantity)
        {
            byte[] req = new byte[4];
            WriteUInt16BE(req, 0, startAddress);
            WriteUInt16BE(req, 2, quantity);

            byte[] pdu = await SendRequestAsync(0x03, req).ConfigureAwait(false);

            if (pdu.Length < 2) throw new Exception("Modbus 响应长度异常（ReadHoldingRegisters）");

            byte byteCount = pdu[1];
            if (byteCount != quantity * 2)
                throw new Exception($"ReadHoldingRegisters 字节数不匹配：期望 {quantity * 2}，实际 {byteCount}");

            if (pdu.Length < 2 + byteCount) throw new Exception("Modbus 响应长度不足（ReadHoldingRegisters）");

            ushort[] regs = new ushort[quantity];
            for (int i = 0; i < quantity; i++)
            {
                int idx = 2 + i * 2;
                regs[i] = ReadUInt16BE(pdu, idx);
            }
            return regs;
        }

        /// <summary>
        /// ReadInputRegistersAsync：读输入寄存器（FC04）
        /// - 本项目 MVP 轮询测量值/状态码使用此函数
        /// </summary>
        private async Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort quantity)
        {
            byte[] req = new byte[4];
            WriteUInt16BE(req, 0, startAddress);
            WriteUInt16BE(req, 2, quantity);

            byte[] pdu = await SendRequestAsync(0x04, req).ConfigureAwait(false);

            if (pdu.Length < 2) throw new Exception("Modbus 响应长度异常（ReadInputRegisters）");

            byte byteCount = pdu[1];

            // 严格检查：便于联调定位点表/数量问题
            if (byteCount != quantity * 2)
                throw new Exception($"ReadInputRegisters 字节数不匹配：期望 {quantity * 2}，实际 {byteCount}");

            if (pdu.Length < 2 + byteCount) throw new Exception("Modbus 响应长度不足（ReadInputRegisters）");

            ushort[] regs = new ushort[quantity];
            for (int i = 0; i < quantity; i++)
            {
                int idx = 2 + i * 2;
                regs[i] = ReadUInt16BE(pdu, idx);
            }
            return regs;
        }
        #endregion

        #region ========== 核心协议区：发送/接收 Modbus TCP ADU（MBAP + PDU） ==========
        /// <summary>
        /// SendRequestAsync：发送一个 Modbus 请求，并读取返回的 PDU
        ///
        /// 输入：
        /// - functionCode：功能码（例如 0x04）
        /// - requestData：PDU 数据区（不含 FC）
        ///
        /// 输出：
        /// - 返回“响应 PDU”（含 FC），即 pdu[0] == functionCode（正常）或 functionCode|0x80（异常）
        ///
        /// 异常处理：
        /// - 若返回异常响应（FC|0x80），抛异常并包含 exception code
        /// - 若 TID 不匹配、长度不匹配、网络断开等，抛异常
        /// </summary>
        private async Task<byte[]> SendRequestAsync(byte functionCode, byte[] requestData)
        {
            EnsureConnected();
            if (_stream == null) throw new InvalidOperationException("NetworkStream 为空");

            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // ---------- 构造 MBAP ----------
                // MBAP: TID(2) PID(2=0) LEN(2) UID(1)
                // LEN = UID(1) + PDU(FC+Data)
                ushort tid = unchecked(++_transactionId);
                ushort pid = 0x0000;
                ushort len = (ushort)(1 + 1 + requestData.Length);

                // ADU = MBAP(7) + PDU(FC+Data)
                byte[] adu = new byte[7 + 1 + requestData.Length];
                WriteUInt16BE(adu, 0, tid);
                WriteUInt16BE(adu, 2, pid);
                WriteUInt16BE(adu, 4, len);
                adu[6] = UnitId;
                adu[7] = functionCode;
                Buffer.BlockCopy(requestData, 0, adu, 8, requestData.Length);

                // ---------- 发送 ----------
                using (var cts = new CancellationTokenSource(IoTimeoutMs))
                {
                    await _stream.WriteAsync(adu, 0, adu.Length, cts.Token).ConfigureAwait(false);
                }

                // ---------- 读取响应 MBAP ----------
                byte[] mbap = new byte[7];
                using (var cts = new CancellationTokenSource(IoTimeoutMs))
                {
                    await ReadExactlyAsync(_stream, mbap, 0, 7, cts.Token).ConfigureAwait(false);
                }

                ushort rTid = ReadUInt16BE(mbap, 0);
                ushort rPid = ReadUInt16BE(mbap, 2);
                ushort rLen = ReadUInt16BE(mbap, 4);
                byte rUid = mbap[6];

                if (rPid != 0) throw new Exception("Modbus 响应 PID 非 0");

                // rLen = UID(1) + PDU(N) => PDU 长度 = rLen - 1
                int pduLen = rLen - 1;
                if (pduLen <= 0) throw new Exception("Modbus 响应长度字段异常");

                // ---------- 读取响应 PDU ----------
                byte[] pdu = new byte[pduLen];
                using (var cts = new CancellationTokenSource(IoTimeoutMs))
                {
                    await ReadExactlyAsync(_stream, pdu, 0, pduLen, cts.Token).ConfigureAwait(false);
                }

                // ---------- 校验 TID（严格模式，便于联调） ----------
                if (rTid != tid)
                    throw new Exception($"Modbus TID 不匹配：Req={tid}, Resp={rTid}");

                // ---------- 异常响应处理 ----------
                // 异常响应格式：FC | 0x80，然后是 exception code
                if (pdu.Length >= 2 && (pdu[0] == (byte)(functionCode | 0x80)))
                {
                    byte exCode = pdu[1];
                    throw new Exception($"Modbus 异常响应：FC=0x{functionCode:X2}, EX=0x{exCode:X2}");
                }

                // 正常响应：pdu[0] 应等于 functionCode
                if (pdu.Length < 1 || pdu[0] != functionCode)
                    throw new Exception($"Modbus 响应功能码不匹配：期望 0x{functionCode:X2}，实际 0x{(pdu.Length > 0 ? pdu[0] : (byte)0):X2}");

                return pdu;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>
        /// ReadExactlyAsync：确保读取 count 字节（网络流 ReadAsync 可能一次读不满）
        /// </summary>
        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int readTotal = 0;
            while (readTotal < count)
            {
                int n = await stream.ReadAsync(buffer, offset + readTotal, count - readTotal, ct).ConfigureAwait(false);
                if (n <= 0) throw new Exception("Modbus 连接已断开（Read 返回 0）");
                readTotal += n;
            }
        }

        /// <summary>
        /// 连接性检查：所有读写入口应先调用
        /// </summary>
        private void EnsureConnected()
        {
            if (!IsConnected || _tcpClient == null || _stream == null)
                throw new InvalidOperationException("PLC 未连接");
        }
        #endregion

        #region ========== 工具函数区：大小端、UInt16、Float32 字节序 ==========
        /// <summary>
        /// 写 UInt16（大端）：Modbus 规定寄存器内为大端字节序
        /// </summary>
        private static void WriteUInt16BE(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value >> 8);
            buf[offset + 1] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// 读 UInt16（大端）
        /// </summary>
        private static ushort ReadUInt16BE(byte[] buf, int offset)
        {
            return (ushort)((buf[offset] << 8) | buf[offset + 1]);
        }

        /// <summary>
        /// Float32（低字在前）：两个寄存器拼 float
        /// - 第 1 寄存器 = LowWord
        /// - 第 2 寄存器 = HighWord
        ///
        /// 说明：与 MotorFinalTest_V3 采用的写入顺序一致（lowWord 在前）。
        /// </summary>
        private static float ToFloat_ABCD(ushort regLow, ushort regHigh)
        {
            uint bits = ((uint)regHigh << 16) | regLow;
            // BitConverter 在 Windows/Intel 上为 little-endian，GetBytes(bits) 与 ToSingle 配套即可
            byte[] bytes = BitConverter.GetBytes(bits);

            return BitConverter.ToSingle(bytes, 0);
        }

        /// <summary>
        /// UInt32（低字在前）：两个寄存器拼无符号 32 位整数
        /// - 第 1 寄存器 = LowWord
        /// - 第 2 寄存器 = HighWord
        /// </summary>
        private static uint ToUInt32_ABCD(ushort regLow, ushort regHigh)
        {
            return ((uint)regHigh << 16) | regLow;
        }

        /// <summary>
        /// Float32（低字在前）：将 float 转为两寄存器
        /// - 第 1 寄存器 = LowWord
        /// - 第 2 寄存器 = HighWord
        /// </summary>
        private static (ushort regLow, ushort regHigh) FromFloat_ABCD(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value); // little-endian: b0 b1 b2 b3
            // 低字：b0 b1；高字：b2 b3
            ushort lo = (ushort)(bytes[0] | (bytes[1] << 8));
            ushort hi = (ushort)(bytes[2] | (bytes[3] << 8));
            return (lo, hi);
        }
        #endregion
    }
}
