using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing;
using System.Numerics;
using System.Text;
using System.Linq;
using System.IO;
using System;


namespace buddhaslice
{
    public static class Program
    {
#if DEBUG
        public const ulong IMG_WIDTH = 3840;
        public const ulong IMG_HEIGHT = 2160;
        public const int MAX_ITER = 10000;
        public const int THREADS = 256;
        public const int CORES = 8;
        public const int DPP = 3;
#else
        public const ulong IMG_WIDTH = 28_800;
        public const ulong IMG_HEIGHT = 16_200;
        public const int MAX_ITER = 50_000;
        public const int THREADS = 1280;
        public const int CORES = 8;
        public const int DPP = 32;
#endif
        public const int SLICE_LEVEL = 8;

        public const int REPORTER_INTERVAL_MS = 500;
        public const int SNAPSHOT_INTERVAL_MS = 360_000;

        public const int MAX_OUTPUT_IMG_SIZE = 536_870_000; // ~512 Megapixel

        public const string PATH_MASK = "mask.png";
        public const string PATH_OUTPUT_DAT = "render.dat";
        public const string PATH_OUTPUT_IMG = "render--tile{0}{1}.png"; // {0} and {1} are placeholders for the tile indices

        public const int THRESHOLD__B_G = 40;
        public const int THRESHOLD__G_R = 300;

        #region PRIVATE FIELDS

        // indexing: [y * WIDTH + x]
        private static BigFuckingAllocator<(int Iterations_R, short Iterations_G, short Iterations_B)> _image;
        private static double[] _progress = new double[THREADS];
        private static bool[,] _mask;
        private static bool _isrunning = true;

        #endregion
        #region ENTRY POINT / SCHEDULING / LOAD+SAVE

        public static async Task Main(string[] _)
        {
            Console.WriteLine($@"
RENDER SETTINGS:
    WIDTH:       {IMG_WIDTH}px
    HEIGHT:      {IMG_HEIGHT}px
    ITERATIONS:  {MAX_ITER}
    THREADS:     {THREADS}
    CORES:       {CORES}
    DPP:         {DPP}
    SLICE LEVEL: {SLICE_LEVEL}
");
            try
            {
                await Task.Factory.StartNew(ProgressReporterTask);

                InitializeMaskAndImage();

                await CreateRenderTask();

                _isrunning = false;

                SaveTiledPNG();
                SaveRaw();
            }
            catch (Exception ex)
            when (!Debugger.IsAttached)
            {
                _isrunning = false;

                StringBuilder sb = new StringBuilder();

                while (ex != null)
                {
                    sb.Insert(0, $"[{ex.GetType()}] {ex.Message}\n{ex.StackTrace}\n");

                    if (ex is AggregateException aex)
                        ex = aex.Flatten();

                    ex = ex.InnerException;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n{sb}");
                Console.ForegroundColor = ConsoleColor.White;
            }
            finally
            {
                _image.Dispose();
            }
        }

        private static async Task CreateRenderTask()
        {
            ActionBlock<int> block = new ActionBlock<int>(Render, new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = CORES,
                EnsureOrdered = false,
            });

            foreach (int rank in Enumerable.Range(0, THREADS))
                block.Post(rank);

            block.Complete();

            await block.Completion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static async void ProgressReporterTask()
        {
            Stopwatch sw_total = new Stopwatch();
            Stopwatch sw_report = new Stopwatch();
            Stopwatch sw_save = new Stopwatch();

            sw_total.Start();
            sw_report.Start();
            sw_save.Start();

            do
            {
                TimeSpan elapsed = sw_total.Elapsed;
                void print(string msg) => Console.Write($"TIME: {DateTime.Now:HH:mm:ss.ff}     ELAPSED: {elapsed:dd':'hh':'mm':'ss'.'fff}     {msg}");

                if (sw_report.ElapsedMilliseconds >= REPORTER_INTERVAL_MS)
                {
                    double progr = _progress.Sum() / THREADS;
                    string est_total = progr < 1e-5 ? "∞" : $"{TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / progr) - elapsed:dd':'hh':'mm':'ss}";

                    print($"REMAINING: {est_total}     PROGRESS: {progr * 100,6:F3}%\r");

                    sw_report.Restart();
                }
                else if (sw_save.ElapsedMilliseconds >= SNAPSHOT_INTERVAL_MS)
                {
                    Console.WriteLine();
                    print($"SAVING SNAPSHOT ....\n");

                    SaveTiledPNG();
                    sw_save.Restart();
                }
                else
                    await Task.Delay(REPORTER_INTERVAL_MS / 3);
            }   
            while (_isrunning);

            Console.WriteLine("\n---------- FINISHED RENDERING ----------\n");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void InitializeMaskAndImage()
        {
            _image = new BigFuckingAllocator<(int, short, short)>(IMG_WIDTH * IMG_HEIGHT);

            using Bitmap mask = (Bitmap)Image.FromFile(PATH_MASK);
            int w = mask.Width;
            int h = mask.Height;

            _mask = new bool[w, h];

            BitmapData dat = mask.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            byte* ptr = (byte*)dat.Scan0;

            Parallel.For(0, w * h, i => _mask[i % w, i / w] = ((double)ptr[i * 3 + 0] + ptr[i * 3 + 1] + ptr[i * 3 + 2]) > 0);

            mask.UnlockBits(dat);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SaveTiledPNG()
        {
            // TODO : improve this whole method with AVX2-instructions.

            int tiles = 1;

            while (IMG_WIDTH * IMG_HEIGHT / (ulong)(tiles * tiles) > MAX_OUTPUT_IMG_SIZE)
                ++tiles;

            int max(params int[] v) => v.Max();
            uint clamp(double v) => (uint)Math.Max(0, Math.Min(v, 255));
            double brightest = 0;

            for (ulong i = 0; i < IMG_WIDTH * IMG_HEIGHT; ++i)
            {
                var pixel = _image[i];

                brightest = Math.Max(brightest, max(pixel->Iterations_B, pixel->Iterations_G, pixel->Iterations_R));
            }

            brightest = 6000 / Math.Sqrt(brightest);

            Bitmap[,] bmp = new ImageTiler<(int r, short g, short b)>(_image, pixel =>
            {
                double r = Math.Sqrt(pixel.r) * brightest;
                double g = Math.Sqrt(pixel.g) * brightest;
                double b = Math.Sqrt(pixel.b) * brightest;

                return 0xff000000u
                     | (clamp(r) << 16)
                     | (clamp(g) << 8)
                     | clamp(b);
            }).GenerateTiles((tiles, tiles), ((int)IMG_WIDTH, (int)IMG_HEIGHT));

            for (int x = 0; x < tiles; ++x)
                for (int y = 0; y < tiles; ++y)
                {
                    Console.WriteLine($"Saving image tile (x,y) = ({x},{y})");

                    bmp[x, y].Save(string.Format(PATH_OUTPUT_IMG, x, y), ImageFormat.Png);
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SaveRaw()
        {
            Console.WriteLine("Saving raw memory data.");

            using FileStream fs = new FileStream(PATH_OUTPUT_DAT, FileMode.Create, FileAccess.Write, FileShare.Read);
            using BufferedStream bf = new BufferedStream(fs);
            using BinaryWriter wr = new BinaryWriter(bf);

            wr.Write(IMG_WIDTH);
            wr.Write(IMG_HEIGHT);

            for (ulong i = 0; i < IMG_WIDTH * IMG_HEIGHT; ++i)
            {
                wr.Write(_image[i]->Iterations_R);
                wr.Write(_image[i]->Iterations_G);
                wr.Write(_image[i]->Iterations_B);
            }
        }

        #endregion
        #region RENDER / CALCULATION METHODS

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Render(int rank)
        {
            int w = (int)IMG_WIDTH;
            int x_px_per_thread = w / THREADS;
            int xsteps = rank < THREADS - 1 ? x_px_per_thread : w - x_px_per_thread * (THREADS - 1);
            int w_mask = _mask.GetLength(0);
            int h_mask = _mask.GetLength(1);

            for (int px_rel = 0; px_rel < xsteps; ++px_rel)
            {
                int px = px_rel + rank * x_px_per_thread;
                int px_mask = (int)((double)px / w * w_mask);

                for (int x_dpp = 0; x_dpp < DPP; ++x_dpp)
                {
                    double re = 3.5 * (px * DPP + x_dpp) / w / DPP - 2.5;

                    for (int py = 0; py < (int)IMG_HEIGHT; ++py)
                    {
                        int py_mask = (int)((double)py / IMG_HEIGHT * h_mask);

                        if (_mask[px_mask, py_mask])
                            for (int y_dpp = 0; y_dpp < DPP; ++y_dpp)
                            {
                                double im = 2d * (py * DPP + y_dpp) / IMG_HEIGHT / DPP - 1;

                                Calculate(new Complex(re, im));
                            }
                    }
                }

                _progress[rank] = (double)px_rel / xsteps;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Calculate(Complex c)
        {
            Complex z = c;
            Complex q;
            int iter = SLICE_LEVEL;

            for (int i = 0; i < SLICE_LEVEL; ++i)
                z = z * z + c;

            q = z;

            while (Math.Abs(z.Imaginary) < 2 && Math.Abs(z.Imaginary) < 2 && iter < MAX_ITER)
            {
                z = z * z + c;

                ++iter;
            }

            if (iter < MAX_ITER)
            {
                double x_idx = (q.Real + 2.5) * IMG_WIDTH / 3.5;
                double y_idx = (q.Imaginary + 1) * IMG_HEIGHT / 2;

                if (x_idx >= 0 && x_idx < IMG_WIDTH &&
                    y_idx >= 0 && y_idx < IMG_HEIGHT)
                {
                    ulong idx = (ulong)y_idx * IMG_WIDTH + (ulong)x_idx;

                    if (iter > THRESHOLD__G_R + THRESHOLD__B_G)
                        _image[idx]->Iterations_R += iter - THRESHOLD__G_R - THRESHOLD__B_G;
                    
                    if (iter > THRESHOLD__B_G)
                        _image[idx]->Iterations_G += (short)(Math.Min(iter, THRESHOLD__G_R) - THRESHOLD__B_G);

                    _image[idx]->Iterations_B += (short)Math.Max(0, Math.Min(iter - 5, THRESHOLD__B_G));
                }
            }
        }

        #endregion
    }
}
