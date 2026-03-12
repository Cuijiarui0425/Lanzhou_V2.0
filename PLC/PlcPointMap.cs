using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Lanzhou_v1._0.PLC
{
    #region ========== 点表枚举定义区 ==========
    /// <summary>
    /// PLC 点位所属区域（与 Modbus 地址空间对应）
    /// - Coil            : 0x（可读可写，常用于命令位/输出线圈）
    /// - DiscreteInput   : 1x（只读，常用于状态位/输入触点）
    /// - HoldingRegister : 4x（可读可写，常用于设定值/参数/配置）
    /// - InputRegister   : 3x（只读，常用于测量值/状态码/报警码）
    ///
    /// 注意：不同软件/PLC 文档对 3x/4x 名称可能互换，但 Modbus 功能码对应关系固定：
    /// - FC01 读 Coil
    /// - FC02 读 DiscreteInput
    /// - FC03 读 HoldingRegister
    /// - FC04 读 InputRegister
    /// - FC05/FC15 写 Coil
    /// - FC06/FC16 写 HoldingRegister
    /// </summary>
    public enum PlcArea
    {
        Coil,
        DiscreteInput,
        HoldingRegister,
        InputRegister
    }

    /// <summary>
    /// 点位数据类型（用于解析寄存器数量与后续字节序/缩放）
    /// - Bool    : 1 bit（对应 Coil/DiscreteInput）
    /// - UInt16  : 1 寄存器
    /// - Int16   : 1 寄存器
    /// - UInt32  : 2 寄存器
    /// - Int32   : 2 寄存器
    /// - Float32 : 2 寄存器（IEEE754）
    ///
    /// 关键点：
    /// - Float32/Int32/UInt32 会占用 2 个寄存器，点表必须确保地址连续可用。
    /// - 字节序（ABCD / CDAB / BADC / DCBA）后续可扩展为 Tag 的一个字段（目前默认 ABCD）。
    /// </summary>
    public enum PlcValueType
    {
        Bool,
        UInt16,
        Int16,
        UInt32,
        Int32,
        Float32
    }
    #endregion

    #region ========== 点位描述结构（Tag） ==========
    /// <summary>
    /// PlcTag：点表中单个点位的描述（Key -> 地址与类型）
    ///
    /// 在本项目中，Key 是“双方对齐的语义名称”，例如：
    /// - Cmd_Enable / Cmd_Start / Cmd_Stop / Cmd_ResetAlarm
    /// - St_Ready / St_Running / St_AlarmActive
    /// - StateCode / AlarmCode / Speed / Load / Temp / Time
    ///
    /// 设计目标：
    /// - UI/业务层只写 Key，不直接操作地址，以降低真点表变化造成的代码改动。
    /// - 通过 Scale/Offset 支持工程量换算（例如原始值 -> rpm、N、℃）。
    /// </summary>
    public sealed class PlcTag
    {
        /// <summary>语义 Key（建议唯一；不区分大小写）</summary>
        public string Key { get; set; } = "";

        /// <summary>点位区域：0x/1x/3x/4x</summary>
        public PlcArea Area { get; set; }

        /// <summary>
        /// Modbus 地址（ushort）
        /// 注意：地址口径必须与从站软件一致（Base0/Base1）。
        /// 当前建议采用 Base0（你已在 Modbus Slave 中验证可用）。
        /// </summary>
        public ushort Address { get; set; }

        /// <summary>数据类型</summary>
        public PlcValueType ValueType { get; set; }

        /// <summary>单位（用于显示/文档）</summary>
        public string Unit { get; set; } = "";

        /// <summary>
        /// 线性缩放因子：工程量 = 原始值 * Scale + Offset
        /// 用途示例：PLC 以 0.01rpm 为单位存储，则 Scale=0.01
        /// </summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>线性偏置：工程量 = 原始值 * Scale + Offset</summary>
        public double Offset { get; set; } = 0.0;

        /// <summary>备注说明（用于验收文档/维护人员理解）</summary>
        public string Note { get; set; } = "";

        /// <summary>
        /// 该点占用的寄存器数量（Bool/Int16/UInt16=1；Float32/Int32/UInt32=2）
        /// 关键：用于批量读寄存器时计算长度或做地址冲突检查
        /// </summary>
        public ushort RegisterCount =>
            (ValueType == PlcValueType.Float32 || ValueType == PlcValueType.Int32 || ValueType == PlcValueType.UInt32) ? (ushort)2 : (ushort)1;
    }
    #endregion

    #region ========== 点表容器（Key->Tag 映射） ==========
    /// <summary>
    /// PlcPointMap：点表容器
    /// - Tags：Key（不区分大小写）-> PlcTag
    /// - LoadFromCsv：从输出目录 Config\xxx.csv 读取点表
    ///
    /// 关键设计点（对甲方程序员）：
    /// - 点表采用分号分隔（;），避免备注 Note 中出现逗号导致 CSV 解析错位。
    /// - 允许 # 开头的注释行，便于现场快速标注。
    /// - 支持缺省 Scale/Offset（为空则按 1/0 处理）。
    /// </summary>
    public sealed class PlcPointMap
    {
        /// <summary>
        /// Key->Tag 映射表（StringComparer.OrdinalIgnoreCase：不区分大小写）
        /// </summary>
        public Dictionary<string, PlcTag> Tags { get; } = new Dictionary<string, PlcTag>(StringComparer.OrdinalIgnoreCase);

        #region ======== 解析与加载区（CSV -> Tags） ========
        /// <summary>
        /// 从 CSV 读取点表（相对路径）
        ///
        /// csvRelativePath 示例：@"Config\pointmap_mvp.csv"
        ///
        /// 搜索路径：
        /// - baseDir = AppDomain.CurrentDomain.BaseDirectory
        ///   即程序输出目录（bin\Debug\net8.0-windows...\ 或发布目录）
        ///
        /// CSV 约定列（共 8 列，分号分隔）：
        /// Key;Area;Address;ValueType;Unit;Scale;Offset;Note
        ///
        /// 关键风险点（联调常见问题）：
        /// 1) Copy to output directory 未设置 -> 文件找不到
        /// 2) Area/ValueType 拼写与枚举不一致 -> Enum.Parse 报错
        /// 3) Address 口径与 Base0/Base1 不一致 -> 读写对不上
        /// </summary>
        public static PlcPointMap LoadFromCsv(string csvRelativePath)
        {
            var map = new PlcPointMap();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, csvRelativePath);

            if (!File.Exists(path))
                throw new FileNotFoundException("点表配置文件不存在（请确认已设置 Copy always）", path);

            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i]?.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // 允许注释（用于现场快速标注）
                if (raw.StartsWith("#")) continue;

                // 跳过表头
                if (raw.StartsWith("Key;", StringComparison.OrdinalIgnoreCase)) continue;

                // 使用分号分隔，避免备注中出现逗号导致解析出错
                var parts = raw.Split(';');
                if (parts.Length < 8)
                    throw new FormatException($"点表第 {i + 1} 行字段数不足（应为 8 列）：{raw}");

                var tag = new PlcTag
                {
                    Key = parts[0].Trim(),
                    Area = (PlcArea)Enum.Parse(typeof(PlcArea), parts[1].Trim(), ignoreCase: true),
                    Address = ushort.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
                    ValueType = (PlcValueType)Enum.Parse(typeof(PlcValueType), parts[3].Trim(), ignoreCase: true),
                    Unit = parts[4].Trim(),
                    Scale = ParseDoubleOrDefault(parts[5].Trim(), 1.0),
                    Offset = ParseDoubleOrDefault(parts[6].Trim(), 0.0),
                    Note = parts[7].Trim()
                };

                if (string.IsNullOrWhiteSpace(tag.Key))
                    throw new FormatException($"点表第 {i + 1} 行 Key 为空：{raw}");

                // Key 重复时，后者覆盖前者（便于现场快速覆盖修正）
                map.Tags[tag.Key] = tag;
            }

            return map;
        }
        #endregion

        #region ======== 查询接口区（业务层使用） ========
        /// <summary>
        /// 获取 Tag（按 Key）
        /// - 若 Key 不存在，抛 KeyNotFoundException（利于早发现点表缺失）
        /// </summary>
        public PlcTag Get(string key)
        {
            if (!Tags.TryGetValue(key, out var tag))
                throw new KeyNotFoundException($"点表中未找到 Key：{key}");
            return tag;
        }

        /// <summary>
        /// 可选：安全获取（不抛异常，返回 null）
        /// 适用于“真点表可能缺某些点”的渐进式兼容场景。
        /// 当前项目更推荐 Get() + 明确报错，联调效率更高。
        /// </summary>
        public PlcTag? TryGet(string key)
        {
            Tags.TryGetValue(key, out var tag);
            return tag;
        }
        #endregion

        #region ======== 工具函数区（解析数值） ========
        /// <summary>
        /// 解析 double；失败则返回缺省值 def。
        /// 说明：点表文件建议使用 InvariantCulture（小数点 .）
        /// </summary>
        private static double ParseDoubleOrDefault(string s, double def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            return def;
        }
        #endregion
    }
    #endregion
}
