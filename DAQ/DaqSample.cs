namespace Lanzhou_v1._0.DAQ
{
    public sealed class DaqSample
    {
        /// <summary>相对启动时刻的秒数（由上层定义）。</summary>
        public double TimestampSeconds { get; set; }

        /// <summary>各通道工程量（本项目为 N）。</summary>
        public double[] Channels { get; set; } = new double[0];
    }
}
