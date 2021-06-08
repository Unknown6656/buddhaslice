using System.Runtime.CompilerServices;
using System.Text.Json;


#if DOUBLE_PRECISION
using precision = System.Double;
#else
using precision = System.Single;
#endif

namespace buddhaslice
{
    public struct Settings
    {
        public ulong width;
        public ulong height;
        public Bounds bounds;
        public Mask mask;
        public int max_iter;
        public int slice_offset;
        public int slice_count;
        public int dpp;
        public int cores;
        public ushort threshold_g;
        public ushort threshold_r;
        public int report_interval_ms;
        public ExportSettings export;


        public override string ToString() => JsonSerializer.Serialize(this, new JsonSerializerOptions { IncludeFields = true });
    }

    public struct Mask
    {
        public string path;
        public Bounds bounds;
    }

    public struct Bounds
    {
        public precision left;
        public precision right;
        public precision top;
        public precision bottom;

        public precision Width => right - left;
        public precision Height => bottom - top;


        public Bounds(precision left, precision right, precision top, precision bottom)
        {
            this.left = left;
            this.right = right;
            this.top = top;
            this.bottom = bottom;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(precision x, precision y) => left <= x && x <= right && top <= y && y <= bottom;
    }

    public struct ExportSettings
    {
        public int interval_ms;
        public bool raw;
        public bool png;
        public bool raw_at_end;
        public string path_raw;
        public string path_png;
        public int max_image_size;
    }
}
