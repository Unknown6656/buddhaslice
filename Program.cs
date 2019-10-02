using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Collections.Generic;
using System.Collections;
using System.Threading.Tasks.Dataflow;
using System.Threading.Tasks;
using System.Threading;
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
        public const int MAX_ITER = 5000;
        public const int THREADS = 256;
        public const int CORES = 7;
        public const int DPP = 3;
#else
        public const ulong IMG_WIDTH = 19_200;
        public const ulong IMG_HEIGHT = 10_800;
        public const int MAX_ITER = 5000_0;
        public const int THREADS = 1280;
        public const int CORES = 8;
        public const int DPP = 1;
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

        // indexing: [y * WIDTH + x]
        private static BigFuckingAllocator<(uint Iterations_R, uint Iterations_B, uint Iterations_G)> _image;
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

                SaveSnapshot();
            }
            catch (Exception ex)
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

                    SaveSnapshot();
                    sw_save.Restart();
                }
                else
                    await Task.Delay(REPORTER_INTERVAL_MS / 3);
            }   
            while (_isrunning);

            Console.WriteLine("\n---------- FINISHED RENDERING ----------\n");
        }

        private static unsafe void InitializeMaskAndImage()
        {
            _image = new BigFuckingAllocator<(uint, uint, uint)>(IMG_WIDTH * IMG_HEIGHT);

            using Bitmap mask = (Bitmap)Image.FromFile(PATH_MASK);
            int w = mask.Width;
            int h = mask.Height;

            _mask = new bool[w, h];

            BitmapData dat = mask.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            byte* ptr = (byte*)dat.Scan0;

            Parallel.For(0, w * h, i => _mask[i % w, i / w] = ((double)ptr[i * 3 + 0] + ptr[i * 3 + 1] + ptr[i * 3 + 2]) > 0);

            mask.UnlockBits(dat);
        }

        private static unsafe void SaveWholePNG()
        {
            int w = (int)IMG_WIDTH;
            int h = (int)IMG_HEIGHT;
            using Bitmap bmp_gray = new Bitmap(w, h);
            using Bitmap bmp_color = new Bitmap(w, h);
            BitmapData dat_gray = bmp_gray.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            BitmapData dat_color = bmp_color.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            uint* ptr_gray = (uint*)dat_gray.Scan0;
            uint* ptr_color = (uint*)dat_color.Scan0;

            uint max(params uint[] v) => v.Max();
            uint clamp(double v) => (uint)Math.Max(0, Math.Min(v, 255));

            double brightest = 0;

            for (ulong i = 0; i < IMG_WIDTH * IMG_HEIGHT; ++i)
            {
                var pixel = _image[i];

                brightest = Math.Max(brightest, max(pixel->Iterations_B, pixel->Iterations_G, pixel->Iterations_R));
            }

            brightest = 6000 / Math.Sqrt(brightest);

            Parallel.For(0, w * h, i =>
            {
                var pixel = _image[(ulong)i];
                double r = Math.Sqrt(pixel->Iterations_R) * brightest;
                double g = Math.Sqrt(pixel->Iterations_G) * brightest;
                double b = Math.Sqrt(pixel->Iterations_B) * brightest;

                ptr_gray[i] = 0xff000000
                            | (clamp(r) << 16)
                            | (clamp(g) << 8)
                            | clamp(b);
            });

            bmp_gray.UnlockBits(dat_gray);
            bmp_gray.Save(PATH_OUTPUT_IMG);
        }

        private static unsafe void SaveSnapshot()
        {
            if (IMG_WIDTH * IMG_HEIGHT < 536_870_912) // 512 Megapixel limit
                SaveWholePNG();

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
            uint iter = SLICE_LEVEL;

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
                ulong x_idx = (ulong)((q.Real + 2.5) * IMG_WIDTH / 3.5);
                ulong y_idx = (ulong)((q.Imaginary + 1) * IMG_HEIGHT / 2);

                if (x_idx >= 0 && x_idx < IMG_WIDTH &&
                    y_idx >= 0 && y_idx < IMG_HEIGHT)
                {
                    ulong idx = y_idx * IMG_WIDTH + x_idx;

                    if (iter > THRESHOLD__G_R + THRESHOLD__B_G)
                        _image[idx]->Iterations_R += iter - THRESHOLD__G_R - THRESHOLD__B_G;
                    
                    if (iter > THRESHOLD__B_G)
                        _image[idx]->Iterations_G += Math.Min(iter, THRESHOLD__G_R) - THRESHOLD__B_G;

                    _image[idx]->Iterations_B += Math.Max(0, Math.Min(iter - 5, THRESHOLD__B_G));
                }
            }
        }

        #endregion
    }





    public unsafe readonly struct BigFuckingAllocator<T>
        where T : unmanaged
    {
        public const int MAX_SLICE_SIZE = 128 * 1024 * 1024;

        private readonly int _slicecount;
        private readonly int _slicesize;
        private readonly T*[] _slices;


        public readonly ulong ItemCount { get; }

        public readonly T* this[ulong idx]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => idx < ItemCount ? _slices[idx / (ulong)_slicesize] + idx % (ulong)_slicesize
                                    : throw new ArgumentOutOfRangeException(nameof(idx), $"The index must be smaller than {ItemCount}.");
        }

        static BigFuckingAllocator()
        {
            if (sizeof(T) > MAX_SLICE_SIZE)
                throw new ArgumentException($"The generic parameter type '{typeof(T)}' cannot be used, as it exceeds the {MAX_SLICE_SIZE} byte limit.", "T");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BigFuckingAllocator(ulong item_count)
        {
            ItemCount = item_count;
            _slicesize = MAX_SLICE_SIZE / sizeof(T);
            _slicecount = (int)Math.Ceiling((double)item_count / _slicesize);
            _slices = new T*[_slicecount];

            for (int i = 0; i < _slicecount; ++i)
            {
                int count = i < _slicecount - 1 ? _slicesize : (int)(item_count - (ulong)(i * _slicesize));

                _slices[i] = (T*)Marshal.AllocHGlobal(count * sizeof(T));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Dispose()
        {
            for (int i = 0; i < _slicecount; ++i)
                Marshal.FreeHGlobal((IntPtr)_slices[i]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly IEnumerable<T> AsIEnumerable()
        {
            // copy local fields to prevent future binding
            ulong sz = (ulong)_slicecount;
            ulong c = ItemCount;
            T*[] sl = _slices;

            IEnumerable<T> iterator(Func<ulong, T> func)
            {
                for (ulong i = 0; i < c; ++i)
                    yield return func(i);
            }

            return iterator(i => sl[i / sz][i % sz]);
        }
    }
}
