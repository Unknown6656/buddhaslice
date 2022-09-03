using System.Runtime.CompilerServices;
using System.Text.Json;

namespace buddhaslice;


public sealed record Settings(
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
    ColoringSettings coloring,
    int report_interval_ms,
    ExportSettings export
)
{
    public static Settings DefaultSettings { get; } = new(
        mask: new(
            path: "mask.png",
            bounds: Bounds.DefaultBounds
        ),
        bounds: Bounds.DefaultBounds,
#if DEBUG
        width: 3840,
        height: 2160,
        max_iter: 10000,
        batches: 8,
        dpp: 3,
#else
        width: 28_800,
        height: 16_200,
        max_iter: 50_000,
        batches: 15,
        dpp: 16,
#endif
        cores: -1,
        native: false,
        slice_offset: 8,
        slice_count: 1,
        report_interval_ms: 500,
        coloring: ColoringSettings.DefaultSettings,
        export: new(
            interval_ms: 360_000,
            max_image_size: 536_870_000,
            path_raw: "render.dat",
            path_png: "render--tile{0}{1}.png",
#if DEBUG
            png: true,
            raw: false,
            raw_at_end: false
#else
            png: true,
            raw: true,
            raw_at_end: true
#endif
        )
    );


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

public sealed record ExportSettings(
    int interval_ms,
    bool raw,
    bool png,
    bool raw_at_end,
    string path_raw,
    string path_png,
    int max_image_size
);

public sealed record ColoringSettings(
    precision legacy_threshold_g,
    precision legacy_threshold_r,
    ColoringMode mode,
    precision color_map_start,
    precision color_map_end,
    string? color_map,
    precision amplification
)
{
    public static ColoringSettings DefaultSettings = new(
        legacy_threshold_g: 40,
        legacy_threshold_r: 300,
        mode: ColoringMode.color_map,
        color_map_start: 1,
        color_map_end: 1,
        color_map: "jet",
        amplification: 2
    );

    public override string ToString() => $"{mode}, {color_map}[{color_map_start}:{color_map_end}], {legacy_threshold_g}->{legacy_threshold_r}";
}

public enum ColoringMode
{
    legacy = 0,
    grayscale = 1,
    color_map = 2,
}
