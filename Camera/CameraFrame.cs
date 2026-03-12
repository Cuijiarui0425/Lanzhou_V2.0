using System;
using System.Windows.Media;

namespace Lanzhou_v1._0.Camera
{
    public sealed class CameraFrame
    {
        public required byte[] Pixels { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Stride { get; init; }
        public required PixelFormat PixelFormat { get; init; }
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    }
}
