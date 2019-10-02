using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Collections.Generic;
using System.Collections;
using System.Threading.Tasks.Dataflow;
using System.Threading.Tasks;
using System.Threading;
using System.Drawing.Imaging;
using System.Drawing;
using System.Numerics;
using System.Linq;
using System;
using System.Diagnostics;
using System.IO;

namespace buddhaslice
{
    public static class Program
    {
#if DEBUG
        public const int IMG_WIDTH = 1920 * 2;
        public const int IMG_HEIGHT = 1080 * 2;
        public const int MAX_ITER = 10000;
        public const int THREADS = 256;
        public const int CORES = 8;
        public const int DPP = 3;
#else
        public const int IMG_WIDTH = 19_200;
        public const int IMG_HEIGHT = 10_800;
        public const int MAX_ITER = 5000_0;
        public const int THREADS = 1280;
        public const int CORES = 8;
        public const int DPP = 5;
#endif
        public const int SLICE_LEVEL = 8;

        public const int REPORTER_INTERVAL_MS = 500;
        public const int SNAPSHOT_INTERVAL_MS = 120_000;

        public const string PATH_MASK = "mask.png";
        public const string PATH_OUTPUT_DAT = "render.dat";
        public const string PATH_OUTPUT_IMG = "render.png";
        public const string PATH_OUTPUT_IMG_COLORED = "render-col.png";

        public const int THRESHOLD__B_G = 40;
        public const int THRESHOLD__G_R = 300;

        #region PRIVATE FIELDS

        // indexing: [x, y]
        private static readonly (int Iterations_R, int Iterations_B, int Iterations_G)[] _image = new (int, int, int)[IMG_WIDTH * IMG_HEIGHT];
        private static readonly double[] _progress = new double[THREADS];
        private static bool[,] _mask;
        private static bool _isrunning = true;

        #endregion
        #region ENTRY POINT + SCHEDULING

        [MTAThread]
        public static async Task Main()
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
            LoadMask();

            await Task.Factory.StartNew(ProgressReporterTask);
            await CreateRenderTask();

            _isrunning = false;

            SaveSnapshot();
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

        private static async void ProgressReporterTask()
        {
            Stopwatch sw_total = new Stopwatch();
            Stopwatch sw_report = new Stopwatch();
            Stopwatch sw_save = new Stopwatch();

            sw_total.Start();
            sw_report.Start();
            sw_save.Start();

            void print(string msg) => Console.Write($"{DateTime.Now:HH:mm:ss.ff}      ELAPSED: {sw_total.Elapsed:dd':'hh':'mm':'ss'.'fff}      {msg}");

            while (_isrunning)
                if (sw_report.ElapsedMilliseconds >= REPORTER_INTERVAL_MS)
                {
                    print($"PROGRESS: {_progress.Sum() / THREADS * 100,6:F3}%\r");

                    sw_report.Restart();
                }
                else if (sw_save.ElapsedMilliseconds >= SNAPSHOT_INTERVAL_MS)
                {
                    Console.WriteLine();
                    print($"SAVING SNAPSHOT ....\n");

                    SaveSnapshot();
                    sw_save.Restart();
                }
                else
                    await Task.Delay(REPORTER_INTERVAL_MS / 3);

            Console.WriteLine();
            print("----------  FINISHED!  ----------\n\n");
        }

        #endregion
        #region RENDER / CALCULATION METHODS

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Render(int rank)
        {
            const int X_PX_PER_THREAD = IMG_WIDTH / THREADS;
            int xsteps = rank < THREADS - 1 ? X_PX_PER_THREAD : IMG_WIDTH - X_PX_PER_THREAD * (THREADS - 1);
            int w_mask = _mask.GetLength(0);
            int h_mask = _mask.GetLength(1);

            for (int px_rel = 0; px_rel < xsteps; ++px_rel)
            {
                int px = px_rel + rank * X_PX_PER_THREAD;
                int px_mask = (int)((double)px / IMG_WIDTH * w_mask);

                for (int x_dpp = 0; x_dpp < DPP; ++x_dpp)
                {
                    double re = 3.5 * (px * DPP + x_dpp) / IMG_WIDTH / DPP - 2.5;

                    for (int py = 0; py < IMG_HEIGHT; ++py)
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
        private static void Calculate(Complex c)
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
                long x_idx = (long)((q.Real + 2.5) * IMG_WIDTH / 3.5);
                long y_idx = (long)((q.Imaginary + 1) * IMG_HEIGHT / 2);

                if (x_idx >= 0 && x_idx < IMG_WIDTH &&
                    y_idx >= 0 && y_idx < IMG_HEIGHT)
                {
                    long idx = y_idx * IMG_WIDTH + x_idx;

                    if (iter > THRESHOLD__G_R + THRESHOLD__B_G)
                        _image[idx].Iterations_R += iter - THRESHOLD__G_R - THRESHOLD__B_G;
                    
                    if (iter > THRESHOLD__B_G)
                        _image[idx].Iterations_G += Math.Min(iter, THRESHOLD__G_R) - THRESHOLD__B_G;

                    _image[idx].Iterations_B += Math.Min(iter, THRESHOLD__B_G) - 5;
                }
            }
        }

        #endregion

        private static unsafe void LoadMask()
        {
            using Bitmap mask = (Bitmap)Image.FromFile(PATH_MASK);
            int w = mask.Width;
            int h = mask.Height;

            _mask = new bool[w, h];

            BitmapData dat = mask.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            byte* ptr = (byte*)dat.Scan0;

            Parallel.For(0, w * h, i => _mask[i % w, i / w] = ((double)ptr[i * 3 + 0] + ptr[i * 3 + 1] + ptr[i * 3 + 2]) > 0);

            mask.UnlockBits(dat);
        }

        private static unsafe void SaveSnapshot()
        {
            using var bmp_gray = new Bitmap(IMG_WIDTH, IMG_HEIGHT);
            using var bmp_color = new Bitmap(IMG_WIDTH, IMG_HEIGHT);
            var dat_gray = bmp_gray.LockBits(new Rectangle(0, 0, IMG_WIDTH, IMG_HEIGHT), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var dat_color = bmp_color.LockBits(new Rectangle(0, 0, IMG_WIDTH, IMG_HEIGHT), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var ptr_gray = (uint*)dat_gray.Scan0;
            var ptr_color = (uint*)dat_color.Scan0;
            int max(params int[] v) => v.Max();

            double brightest = 6000 / Math.Sqrt(_image.Max(pixel => max(pixel.Iterations_B, pixel.Iterations_G, pixel.Iterations_R)));

            Parallel.For(0, IMG_WIDTH * IMG_HEIGHT, idx =>
            {
                double r = Math.Sqrt(_image[idx].Iterations_R) * brightest;
                double g = Math.Sqrt(_image[idx].Iterations_G) * brightest;
                double b = Math.Sqrt(_image[idx].Iterations_B) * brightest;

                uint clamp(double v) => (uint)Math.Max(0, Math.Min(v, 255));

                ptr_gray[idx] = 0xff000000
                          | (clamp(r) << 16)
                          | (clamp(g) << 8)
                          | clamp(b);
            });

            bmp_gray.UnlockBits(dat_gray);
            bmp_gray.Save(PATH_OUTPUT_IMG);

            using var fs = new FileStream(PATH_OUTPUT_DAT, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var wr = new BinaryWriter(fs);

            wr.Write(IMG_WIDTH);
            wr.Write(IMG_HEIGHT);

            for (int i = 0; i < IMG_WIDTH * IMG_HEIGHT; ++i)
            {
                wr.Write(_image[i].Iterations_R);
                wr.Write(_image[i].Iterations_G);
                wr.Write(_image[i].Iterations_B);
            }
        }
    }
}
