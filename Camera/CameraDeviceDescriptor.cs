using PvDotNet;
using System;
using System.Reflection;

namespace Lanzhou_v1._0.Camera
{
    /// <summary>
    /// 相机设备描述（用于 UI 列表显示与选择）。
    /// 注意：底层 DeviceInfo 由 eBUS 返回；连接时直接使用。
    /// </summary>
    public sealed class CameraDeviceDescriptor
    {
        public PvDeviceInfo DeviceInfo { get; }
        public string DisplayId { get; }
        public string ConnectionId { get; }

        public CameraDeviceDescriptor(PvDeviceInfo info)
{
    DeviceInfo = info;
    DisplayId = GetStringCompat(info, "DisplayID", "DisplayId", "GetDisplayID");
    ConnectionId = GetStringCompat(info, "ConnectionID", "ConnectionId", "GetConnectionID");
}

private static string GetStringCompat(object obj, params string[] names)
{
    var t = obj.GetType();
    foreach (var n in names)
    {
        var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
        if (p != null && p.PropertyType == typeof(string))
        {
            var v = p.GetValue(obj, null) as string;
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        }

        var m = t.GetMethod(n, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (m != null && m.ReturnType == typeof(string))
        {
            var v = m.Invoke(obj, null) as string;
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        }
    }
    return string.Empty;
}

        public override string ToString()
        {
            // 用于 ListBox 默认显示
            return string.IsNullOrWhiteSpace(DisplayId) ? ConnectionId : DisplayId;
        }
    }
}
