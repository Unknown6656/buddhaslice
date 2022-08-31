using System.Runtime.CompilerServices;
using System.Text.Json;

namespace buddhaslice;


public record struct Settings(
    ulong width,
    ulong height,
    Bounds bounds,
    Mask mask,
    int max_iter,
    int slice_offset,
    int slice_count,
    int dpp,
    int cores,
    precision threshold_g,
    precision threshold_r,
    precision scale_factor,
    bool grayscale,
    int report_interval_ms,
    ExportSettings export
)
{
    public override string ToString() => JsonSerializer.Serialize(this, new JsonSerializerOptions { IncludeFields = true });
}

public record struct Mask(string path, Bounds bounds);

public record struct Bounds(precision left, precision right, precision top, precision bottom)
{
    public precision Width => right - left;
    public precision Height => bottom - top;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(precision x, precision y) => left <= x && x <= right && top <= y && y <= bottom;
}

public record struct ExportSettings(
    int interval_ms,
    bool raw,
    bool png,
    bool raw_at_end,
    string path_raw,
    string path_png,
    int max_image_size
);
