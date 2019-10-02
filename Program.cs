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
        public const int IMG_WIDTH = 1920;
        public const int IMG_HEIGHT = 1080;
        public const int MAX_ITER = 1000;
        public const int CORES = 16;
        public const int DPP = 3;
#else
        public const int IMG_WIDTH = 19_200;
        public const int IMG_HEIGHT = 10_800;
        public const int MAX_ITER = 10_000;
        public const int CORES = 8;
        public const int DPP = 5;
#endif
        public const int THREADS = 1280;
        public const int SLICE_LEVEL = 0;

        public const int REPORTER_INTERVAL_MS = 500;
        public const int SNAPSHOT_INTERVAL_MS = 240_000;


        #region PRIVATE FIELDS

        // indexing: [x, y]
        private static readonly int[,] _image = new int[IMG_WIDTH, IMG_HEIGHT];
        private static readonly double[] _progress = new double[THREADS];
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

            await Task.Factory.StartNew(ProgressReporterTask);
            await CreateRenderTask();

            _isrunning = false;

            SaveImage();
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

                    SaveImage();
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
            const int x_pixels_total = IMG_WIDTH * DPP;
            const int x_pixels_per_thread = x_pixels_total / THREADS;
            int steps = rank < THREADS - 1 ? x_pixels_per_thread : x_pixels_total - (THREADS - 1) * x_pixels_per_thread;

            for (int x = 0; x < steps; ++x)
            {
                double x_abs = x + rank * x_pixels_per_thread;
                double re = 3.5 * x_abs / x_pixels_total - 2.5;

                for (double im = -1; im < 1; im += 2.0 / IMG_HEIGHT / DPP)
                    Calculate(new Complex(re, im));

                _progress[rank] = (double)x / steps;
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
                    _image[x_idx, y_idx] = iter;
            }
        }

        #endregion

        private static unsafe void SaveImage()
        {
            /*
             * EXPLANATION:
             *  render-200.png      escape time [0..200] mapped to 8bit grayscale
             *  render-real.png     escape time [0..MAX_ITER] mapped to 8bit grayscale
             *  render-int32.dat    raw escape time
             */

            using var bmp1 = new Bitmap(IMG_WIDTH, IMG_HEIGHT);
            using var bmp2 = new Bitmap(IMG_WIDTH, IMG_HEIGHT);
            var dat1 = bmp1.LockBits(new Rectangle(0, 0, IMG_WIDTH, IMG_HEIGHT), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var dat2 = bmp2.LockBits(new Rectangle(0, 0, IMG_WIDTH, IMG_HEIGHT), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            uint* ptr1 = (uint*)dat1.Scan0;
            uint* ptr2 = (uint*)dat2.Scan0;

            Parallel.For(0, IMG_WIDTH * IMG_HEIGHT, idx =>
            {
                byte g1 = (byte)Math.Min(_image[idx % IMG_WIDTH, idx / IMG_WIDTH] * 1.275, 255);
                byte g2 = (byte)(_image[idx % IMG_WIDTH, idx / IMG_WIDTH] * 255d / MAX_ITER);

                ptr1[idx] = 0xff000000
                          | ((uint)g1 << 16)
                          | ((uint)g1 << 8)
                          | g1;

                ptr2[idx] = 0xff000000
                          | ((uint)g2 << 16)
                          | ((uint)g2 << 8)
                          | g2;
            });

            bmp1.UnlockBits(dat1);
            bmp2.UnlockBits(dat2);
            bmp1.Save("render-200.png");
            bmp2.Save("render-real.png");

            using var fs = new FileStream("render-int32.dat", FileMode.Create, FileAccess.Write, FileShare.Read);
            using var wr = new BinaryWriter(fs);

            wr.Write(IMG_WIDTH);
            wr.Write(IMG_HEIGHT);

            for (int y = 0; y < IMG_HEIGHT; ++y)
                for (int x = 0; x < IMG_WIDTH; ++x)
                    wr.Write(_image[x, y]);
        }
    }
}
