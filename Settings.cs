
namespace buddhaslice
{
    public readonly struct Settings
    {
        public string path_mask { get; init; }
        public string path_raw { get; init; }
        public string path_out { get; init; }
        public Bounds bounds { get; init; }
        public ulong width { get; init; }
        public ulong height { get; init; }
        public int max_iter { get; init; }
        public int max_image_size { get; init; }
        public int slice { get; init; }
        public int dpp { get; init; }
        public int threads { get; init; }
        public int cores { get; init; }
        public int threshold_g { get; init; }
        public int threshold_r { get; init; }
        public bool claim_memory { get; init; }
        public int report_interval_ms { get; init; }
        public ExportSettings export { get; init; }
    }

    public readonly struct Bounds
    {
        public Bound image { get; init; }
        public Bound mask { get; init; }
    }

    public readonly struct Bound
    {
        public double left { get; init; }
        public double right { get; init; }
        public double top { get; init; }
        public double bottom { get; init; }


        public Bound(double left, double right, double top, double bottom)
        {
            this.left = left;
            this.right = right;
            this.top = top;
            this.bottom = bottom;
        }
    }

    public readonly struct ExportSettings
    {
        public int interval_ms { get; init; }
        public bool raw { get; init; }
        public bool png { get; init; }
        public bool raw_at_end { get; init; }
    }
}
