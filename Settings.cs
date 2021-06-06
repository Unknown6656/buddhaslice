
namespace buddhaslice
{
    public sealed record Settings(
        string path_mask,
        string path_raw,
        string path_out,
        Bounds bounds,
        int width,
        int height,
        int max_iter,
        int max_image_size,
        int slice,
        int dpp,
        int threads,
        int cores,
        double threshold_g,
        double threshold_h,
        bool claim_memory,
        int report_interval_ms,
        ExportSettings export
    );

    public sealed record Bounds(Bound image, Bound mask);

    public sealed record Bound(double left, double right, double top, double bottom);

    public sealed record ExportSettings(int interval_ms, bool raw, bool png, bool raw_at_end);
}
