using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Lanzhou_v1._0.PLC
{
    public sealed class ServoAlarmInfo
    {
        public int CodeInt { get; set; }
        public string CodeHex { get; set; } = "";
        public string Note { get; set; } = "";
        public string Name { get; set; } = "";
        public string Cause { get; set; } = "";
        public string Action { get; set; } = "";
    }

    /// <summary>
    /// 伺服报警字典（来自交互表-最终版.xlsx 的“伺服报警”页）
    /// 文件：Config\servo_alarm_map.csv
    /// 列：CodeHex;CodeInt;Note;Name;Cause;Action
    /// </summary>
    public sealed class ServoAlarmCatalog
    {
        private readonly Dictionary<int, ServoAlarmInfo> _byCode = new();

        public int Count => _byCode.Count;

        public bool TryGet(int code, out ServoAlarmInfo info) => _byCode.TryGetValue(code, out info!);

        public static ServoAlarmCatalog LoadFromCsv(string csvRelativePath)
        {
            var cat = new ServoAlarmCatalog();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, csvRelativePath);

            if (!File.Exists(path))
                throw new FileNotFoundException("伺服报警配置文件不存在（请确认 Copy always）", path);

            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i]?.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.StartsWith("#")) continue;
                if (raw.StartsWith("CodeHex;", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = raw.Split(';');
                if (parts.Length < 6)
                    throw new FormatException($"伺服报警表第 {i + 1} 行字段数不足（应为 6 列）：{raw}");

                string codeHex = parts[0].Trim();
                string codeIntRaw = parts[1].Trim();

                int codeInt;
                if (!int.TryParse(codeIntRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out codeInt))
                {
                    // 兜底：允许仅给 hex
                    codeInt = ParseHexToInt(codeHex);
                }

                var info = new ServoAlarmInfo
                {
                    CodeHex = codeHex,
                    CodeInt = codeInt,
                    Note = parts[2].Trim(),
                    Name = parts[3].Trim(),
                    Cause = parts[4].Trim(),
                    Action = parts[5].Trim(),
                };

                cat._byCode[codeInt] = info;
            }

            return cat;
        }

        private static int ParseHexToInt(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return 0;
            hex = hex.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                return v;
            return 0;
        }

        public static string ToHex(int code)
        {
            if (code < 0) return code.ToString(CultureInfo.InvariantCulture);
            return "0x" + code.ToString("X4", CultureInfo.InvariantCulture);
        }
    }
}
