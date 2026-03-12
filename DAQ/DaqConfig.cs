using System;
using System.Linq;

namespace Lanzhou_v1._0.DAQ
{
    /// <summary>
    /// DAQ 配置（面向 PCIe-1805 的 AI 采样）
    /// 说明：
    /// - 本项目只用 AI0（载荷电压 0~10V），再通过线性标定换算到 N。
    /// - SamplesPerSecond 与 AvgWindow 由 UI 可调；其它关键参数可按项目要求固化。
    /// </summary>
    public sealed class DaqConfig
    {
        public int DeviceId { get; set; } = 1;

        public int ChannelStart { get; set; } = 0;
        public int ChannelCount { get; set; } = 1;

        /// <summary>目标采样率（Hz）。Instant 方式下为“尝试值”，受 Windows 调度影响。</summary>
        public int SamplesPerSecond { get; set; } = 1000;

        /// <summary>N 点平均窗口（>=1）。</summary>
        public int AvgWindow { get; set; } = 20;

        /// <summary>标定系数数组：N/V（每通道一个）。</summary>
        public double[] ScaleFactors { get; set; } = Array.Empty<double>();

        /// <summary>标定偏置数组：N（每通道一个）。</summary>
        public double[] Offsets { get; set; } = Array.Empty<double>();

        /// <summary>
        /// 兼容旧逻辑：section 与 samplesPerChannel 在 Buffered 方案中使用；
        /// 目前采用 Instant 轮询实现，这两个字段只用于保持接口一致。
        /// </summary>
        public int SectionPerChannel { get; set; } = 0;
        public int SamplesPerChannel { get; set; } = 0;

        public void Normalize()
        {
            ChannelCount = Math.Max(1, ChannelCount);
            SamplesPerSecond = Math.Max(1, SamplesPerSecond);
            AvgWindow = Math.Max(1, AvgWindow);

            if (ScaleFactors == null || ScaleFactors.Length != ChannelCount)
                ScaleFactors = Enumerable.Repeat(1.0, ChannelCount).ToArray();

            if (Offsets == null || Offsets.Length != ChannelCount)
                Offsets = Enumerable.Repeat(0.0, ChannelCount).ToArray();
        }
    }
}
