using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace Lanzhou_v1._0.Sensors
{
    /// <summary>
    /// 多路力值/应变片变送器（RS485 Modbus-RTU）采集：
    /// - 通道1：载荷 Fn（单位按变送器配置；上位机按“载荷”理解）
    /// - 通道2：温度（甲方要求按 °C 解释；上位机直接把该通道当温度）
    ///
    /// 寄存器约定（来自说明书 V1.70）：
    /// - 内部地址 0/1：第1路数据 低字/高字（32bit 有符号）
    /// - 内部地址 2/3：第2路数据 低字/高字（32bit 有符号）
    /// 读取方式：功能码 0x03，起始地址 0，数量 4（一次拿到通道1&2）
    /// </summary>
    public sealed class StrainTransmitterService : IDisposable
    {
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private readonly object _lock = new();
        private bool _hasValue;
        private double _ff;
        private double _temp;
        private string _status = "Serial: Not Started";

        public string StatusText => _status;
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        public void Start(string portName, int baudRate = 115200, byte slaveId = 1, int pollIntervalMs = 100)
        {
            Stop();

            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 200,
                WriteTimeout = 200,
                DtrEnable = false,
                RtsEnable = false,
                NewLine = "\r\n"
            };

            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            _status = $"Serial: Opened ({portName}, {baudRate}, Slave={slaveId})";

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _loopTask = Task.Run(() => Loop(slaveId, pollIntervalMs, token), token);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts = null;

            try { _loopTask?.Wait(200); } catch { /* ignore */ }
            _loopTask = null;

            try
            {
                if (_port != null)
                {
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                }
            }
            catch { /* ignore */ }

            _port = null;
            _status = "Serial: Stopped";
        }

        public bool TryGetLatest(out double ff, out double temp)
        {
            lock (_lock)
            {
                ff = _ff;
                temp = _temp;
                return _hasValue;
            }
        }

        private void Loop(byte slaveId, int pollIntervalMs, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_port == null || !_port.IsOpen)
                    {
                        _status = "Serial: Not Open";
                        Thread.Sleep(200);
                        continue;
                    }

                    // 读取保持寄存器：起始 0，数量 4（通道1/2 各 32bit）
                    ushort[] regs;
                    if (TryReadHoldingRegisters(slaveId, 0, 4, out regs))
                    {
                        int ch1 = CombineToInt32(lowWord: regs[0], highWord: regs[1]);
                        int ch2 = CombineToInt32(lowWord: regs[2], highWord: regs[3]);

                        lock (_lock)
                        {
                            _ff = ch1;
                            _temp = ch2;
                            _hasValue = true;
                        }

                        _status = "Serial: OK";
                    }
                    else
                    {
                        _status = "Serial: Read Failed";
                    }
                }
                catch (Exception ex)
                {
                    _status = "Serial: Error - " + ex.Message;
                }

                try { Thread.Sleep(pollIntervalMs); }
                catch { /* ignore */ }
            }
        }

        private bool TryReadHoldingRegisters(byte slaveId, ushort startAddress, ushort quantity, out ushort[] regs)
        {
            regs = Array.Empty<ushort>();
            if (_port == null) return false;

            // 构造请求帧
            byte[] req = new byte[8];
            req[0] = slaveId;
            req[1] = 0x03;
            req[2] = (byte)(startAddress >> 8);
            req[3] = (byte)(startAddress & 0xFF);
            req[4] = (byte)(quantity >> 8);
            req[5] = (byte)(quantity & 0xFF);
            ushort crc = Crc16Modbus(req, 0, 6);
            req[6] = (byte)(crc & 0xFF);       // CRC Lo
            req[7] = (byte)(crc >> 8);         // CRC Hi

            _port.DiscardInBuffer();
            _port.Write(req, 0, req.Length);

            // 响应长度： 1+1+1 + 2*quantity +2
            int expected = 3 + 2 * quantity + 2;
            byte[] resp = new byte[expected];

            int read = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(250);
            while (read < expected && DateTime.UtcNow < deadline)
            {
                try
                {
                    int n = _port.Read(resp, read, expected - read);
                    if (n > 0) read += n;
                }
                catch (TimeoutException)
                {
                    // 允许短超时，继续读取
                }
            }

            if (read < expected) return false;
            if (resp[0] != slaveId) return false;
            if (resp[1] != 0x03) return false;
            if (resp[2] != 2 * quantity) return false;

            // CRC 校验
            ushort crcCalc = Crc16Modbus(resp, 0, expected - 2);
            ushort crcResp = (ushort)(resp[expected - 2] | (resp[expected - 1] << 8));
            if (crcCalc != crcResp) return false;

            regs = new ushort[quantity];
            int offset = 3;
            for (int i = 0; i < quantity; i++)
            {
                regs[i] = (ushort)((resp[offset + i * 2] << 8) | resp[offset + i * 2 + 1]);
            }

            return true;
        }

        private static int CombineToInt32(ushort lowWord, ushort highWord)
        {
            uint combined = ((uint)highWord << 16) | (uint)lowWord;
            return unchecked((int)combined);
        }

        /// <summary>CRC16/MODBUS (poly 0xA001, init 0xFFFF)</summary>
        private static ushort Crc16Modbus(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int b = 0; b < 8; b++)
                {
                    bool lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb) crc ^= 0xA001;
                }
            }
            return crc;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
