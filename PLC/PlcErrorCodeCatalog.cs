using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Lanzhou_v1._0.PLC
{
    /// <summary>
    /// PLC 自定义错误/报警码字典（来自交互表-最终版.xlsx 的“信息交互”页）
    /// 文件：Config\plc_error_code_map.csv
    /// 列：Code;Text
    /// </summary>
    public sealed class PlcErrorCodeCatalog
    {
        private readonly Dictionary<int, string> _byCode = new();

        public int Count => _byCode.Count;

        public bool TryGet(int code, out string text) => _byCode.TryGetValue(code, out text!);

        public static PlcErrorCodeCatalog LoadFromCsv(string csvRelativePath)
        {
            var cat = new PlcErrorCodeCatalog();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, csvRelativePath);

            if (!File.Exists(path))
                throw new FileNotFoundException("PLC 报警码配置文件不存在（请确认 Copy always）", path);

            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i]?.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.StartsWith("#")) continue;
                if (raw.StartsWith("Code;", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = raw.Split(';');
                if (parts.Length < 2)
                    throw new FormatException($"PLC 报警码表第 {i + 1} 行字段数不足（应为 2 列）：{raw}");

                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                    continue;

                var text = parts[1].Trim();
                cat._byCode[code] = text;
            }

            return cat;
        }
    }
}
