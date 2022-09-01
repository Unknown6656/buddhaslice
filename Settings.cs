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
    int batches,
    int cores,
    bool native,
    precision threshold_g,
    precision threshold_r,
    precision scale_factor,
    bool grayscale,
    string? color_map,
    bool reverse_color_map,
    int report_interval_ms,
    ExportSettings export
)
{
    public static Settings DefaultSettings { get; } = new()
    {
        mask = new()
        {
            bounds = Bounds.DefaultBounds,
            path = "mask.png",
        },
        bounds = Bounds.DefaultBounds,
#if DEBUG
        width = 3840,
        height = 2160,
        max_iter = 10000,
        batches = 8,
        dpp = 3,
#else
        width = 28_800,
        height = 16_200,
        max_iter = 50_000,
        cores = 8,
        dpp = 16,
#endif
        native = false,
        slice_offset = 8,
        slice_count = 1,
        report_interval_ms = 500,
        threshold_g = 40,
        threshold_r = 300,
        color_map = "jet",
        reverse_color_map = true,
        export = new()
        {
            interval_ms = 360_000,
            max_image_size = 536_870_000,
            path_raw = "render.dat",
            path_png = "render--tile{0}{1}.png",
#if DEBUG
            png = true,
            raw = false,
            raw_at_end = false,
#else
            png = true,
            raw = true,
            raw_at_end = true,
#endif
        },
    };


    public override string ToString() => JsonSerializer.Serialize(this, new JsonSerializerOptions { IncludeFields = true });
}

public record struct Mask(string path, Bounds bounds);

public record struct Bounds(precision left, precision right, precision top, precision bottom)
{
    public static Bounds DefaultBounds = new(-2f, -1.1f, .75f, 1.1f);

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
