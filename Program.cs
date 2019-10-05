#nullable enable

using System.Runtime.Intrinsics.X86;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;
using System.Drawing.Imaging;
using System.Drawing;
using System.Numerics;
using System.Text;
using System.Linq;
using System.IO;
using System;

using Newtonsoft.Json;


namespace buddhaslice
{
    public static class Program
    {
        public const string PATH_CONFIG = "settings.json";

        #region FIELDS / SETTINGS

        private static ulong IMG_WIDTH;
        private static ulong IMG_HEIGHT;
        private static int MAX_ITER;
        private static int THREADS;
        private static int CORES;
        private static int DPP;
        private static int SLICE_LEVEL;
        private static int REPORTER_INTERVAL_MS;
        private static int SNAPSHOT_INTERVAL_MS;
        private static int MAX_OUTPUT_IMG_SIZE;
        private static string PATH_MASK;
        private static string PATH_OUTPUT_DAT;
        private static string PATH_OUTPUT_IMG;
        private static bool EXPORT_RAW;
        private static bool EXPORT_PNG;
        private static int THRESHOLD__B_G;
        private static int THRESHOLD__G_R;
        private static (double left, double top, double right, double bottom) MASK_BOUNDS;
        private static (double left, double top, double right, double bottom) IMAGE_BOUNDS;

        private static ConcurrentQueue<(string name, bool finished)> _queue_rendered = new ConcurrentQueue<(string, bool)>();

        // indexing: [y * WIDTH + x]
        private static BigFuckingAllocator<(int Iterations_R, int Iterations_G, int Iterations_B)> _image;
        private static double[] _progress;
        private static bool[,] _mask;
        private static bool _isrunning = true;

        #endregion
        #region ENTRY POINT / SCHEDULING / LOAD+SAVE

        public static async Task Main(string[] _)
        {
            Task? _reporter = null;
            void exit()
            {
                _isrunning = false;
                _image.Dispose();

                if (_reporter is { })
                    using (_reporter)
                        _reporter.Wait();

                Console.CursorVisible = true;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.BackgroundColor = ConsoleColor.Black;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, e) => exit();

            try
            {
                LoadSettings();

                _reporter = await Task.Factory.StartNew(ProgressReporterTask);

                InitializeMaskAndImage();
                WarmUpMethods();

                await CreateRenderTask();

                SaveSnapshot();
            }
            catch (Exception? ex)
            when (!Debugger.IsAttached)
            {
                _isrunning = false;

                if (_reporter is { })
                    using (_reporter)
                        _reporter.Wait();

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
            }

            exit();
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
        private static async Task ProgressReporterTask()
        {
            const int WIDTH = 150;

            Console.Title = "BUDDHASLICE - by Unknown6656";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Clear();
            Console.CursorVisible = false;
            Console.BufferWidth = Math.Max(WIDTH, Console.BufferWidth);
            Console.WindowWidth = WIDTH;
            Console.BufferHeight = Math.Max(WIDTH * 2, Console.BufferHeight);
            Console.WindowHeight = Math.Max(WIDTH / 3, Console.WindowHeight);
            Console.WriteLine(new string('═', WIDTH));

            int top_progress = Console.CursorTop;
            int left_progress = WIDTH / 2 + 1;

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($@"
  RENDER CONFIGURATION:
        WIDTH:             {IMG_WIDTH}px
        HEIGHT:            {IMG_HEIGHT}px
        ITERATIONS:        {MAX_ITER}
        THREADS:           {THREADS}
        CORES:             {CORES} (+ 1)
        DPP:               {DPP}
        SLICE LEVEL:       {SLICE_LEVEL}
        SNAPSHOT INTERVAL: {SNAPSHOT_INTERVAL_MS / 1000d}s
        MAX. IMAGE SIZE:   {MAX_OUTPUT_IMG_SIZE / (1024d * 1024):F2} MP
        MASK PATH:         ""{PATH_MASK}""
        OUTPUT PNG PATH:   ""{PATH_OUTPUT_IMG}""
        OUTPUT RAW PATH:   ""{PATH_OUTPUT_DAT}""
        EXPORT PNG:        {EXPORT_PNG}
        EXPORT RAW:        {EXPORT_RAW}
        THRESHOLD B -> G:  {THRESHOLD__B_G}
        THRESHOLD G -> R:  {THRESHOLD__G_R}
");

            int tmp = Console.CursorTop;

            Console.CursorLeft = WIDTH / 2 - 1;
            Console.ForegroundColor = ConsoleColor.Gray;

            for (int t = top_progress; t < tmp; ++t)
            {
                Console.CursorTop = t;
                Console.Write('║');
                Console.CursorLeft--;
            }

            ++top_progress;

            Console.CursorTop = tmp;
            Console.CursorLeft = 0;
            Console.WriteLine(new string('═', WIDTH));
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  THREADS:\n");

            int top_threads = Console.CursorTop;
            (int l, int t) get_thread_pos(int rank) => (2 + (rank * 6) % (WIDTH - 6), top_threads + rank / (WIDTH / 6 - 1));

            Console.CursorTop = get_thread_pos(THREADS + 1).t + 2;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(new string('═', WIDTH));
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  EXPORTED DATA:\n");

            int top_exports = Console.CursorTop;

            Stopwatch sw_total = new Stopwatch();
            Stopwatch sw_report = new Stopwatch();
            Stopwatch sw_save = new Stopwatch();

            sw_total.Start();
            sw_report.Start();
            sw_save.Start();

            do
                if(sw_save.ElapsedMilliseconds >= SNAPSHOT_INTERVAL_MS)
                {
                    sw_save.Stop();
                    sw_save.Reset();

                    Console.CursorLeft = 6;
                    Console.CursorTop = top_exports;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write($"Saving snapshot ...");

                    ++top_exports;

                    _ = Task.Factory.StartNew(() =>
                    {
                        SaveSnapshot();

                        sw_save.Restart();
                    }); // we do NOT want to await this task!
                }
                else if (sw_report.ElapsedMilliseconds >= REPORTER_INTERVAL_MS)
                {
                    TimeSpan elapsed = sw_total.Elapsed;
                    double progr = _progress.Sum() / THREADS;
                    string est_total = progr < 1e-5 ? "∞" : $"{TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / progr) - elapsed:dd':'hh':'mm':'ss}";

                    Console.CursorLeft = left_progress;
                    Console.CursorTop = top_progress;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine($"CURRENT TIME:                  {DateTime.Now:HH:mm:ss.ff}");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"ELAPSED TIME:               {elapsed:dd':'hh':'mm':'ss'.'ff}");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"REMAINING TIME (ESTIMATED): {est_total}.000");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"CURRENT MEMORY FOOTPRINT:   {Process.GetCurrentProcess().WorkingSet64 / 1073741824d:N2} GB");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"TOTAL PROGRESS:             {progr * 100,9:N5} %");

                    for (int i = 0; i < THREADS; ++i)
                    {
                        (Console.CursorLeft, Console.CursorTop) = get_thread_pos(i);
                        double p = Math.Round(_progress[i] * 100, 1);

                        if (p == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write(" 0.0%");
                        }
                        else if (p < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write($"{p,4:N1}%");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.Write(" 100%");
                        }
                    }

                    while (_queue_rendered.TryDequeue(out (string file, bool finished) export))
                    {
                        Console.CursorLeft = 6;
                        Console.CursorTop = top_exports;
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write($"{(export.finished ? "Finished" : "Began")} exporting '{export.file}'.");

                        ++top_exports;
                    }
                }
                else
                    await Task.Delay(REPORTER_INTERVAL_MS / 3);
            while (_isrunning);

            Console.CursorLeft = 0;
            Console.CursorTop = top_exports;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(new string('═', WIDTH));
            Console.CursorTop++;

            sw_total.Stop();

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"     TOTAL RENDER TIME: {sw_total.Elapsed:dd':'hh':'mm':'ss'.'fff}\n");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(new string('═', WIDTH));
            Console.CursorTop++;
        }

        private static void WarmUpMethods()
        {
            Type t = typeof(Program);

            foreach (string m in new[]
            {
                nameof(Render),
                nameof(Calculate),
                nameof(SaveTiledPNG)
            })
                RuntimeHelpers.PrepareMethod(t.GetMethod(m, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.MethodHandle);
        }

        private static void LoadDefaultSettings()
        {
            IMAGE_BOUNDS =
            MASK_BOUNDS = (-2, -1.1, .75, 1.1);
#if DEBUG
            IMG_WIDTH = 3840;
            IMG_HEIGHT = 2160;
            MAX_ITER = 10000;
            THREADS = 256;
            CORES = 8;
            DPP = 3;
#else
            IMG_WIDTH = 28_800;
            IMG_HEIGHT = 16_200;
            MAX_ITER = 50_000;
            THREADS = 1280;
            CORES = 8;
            DPP = 16;
#endif
            SLICE_LEVEL = 8;

            REPORTER_INTERVAL_MS = 500;
            SNAPSHOT_INTERVAL_MS = 360_000;

            MAX_OUTPUT_IMG_SIZE = 536_870_000;

            PATH_MASK = "mask.png";
            PATH_OUTPUT_DAT = "render.dat";
            PATH_OUTPUT_IMG = "render--tile{0}{1}.png";

            EXPORT_RAW = true;
            EXPORT_PNG = true;

            THRESHOLD__B_G = 40;
            THRESHOLD__G_R = 300;
        }

        private static void LoadSettings()
        {
            Console.WriteLine("Loading settings...");

            try
            {
                string json = File.ReadAllText(PATH_CONFIG);
                dynamic config = JsonConvert.DeserializeObject(json);
                dynamic b_image = config.bounds.image;
                dynamic b_mask = config.bounds.mask;

                IMAGE_BOUNDS = (b_image.left, b_image.top, b_image.right, b_image.bottom);
                MASK_BOUNDS = (b_mask.left, b_mask.top, b_mask.right, b_mask.bottom);
                IMG_WIDTH = config.width;
                IMG_HEIGHT = config.height;
                MAX_ITER = config.max_iter;
                THREADS = config.threads;
                CORES = config.cores;
                DPP = config.dpp;
                SLICE_LEVEL = config.slice;
                REPORTER_INTERVAL_MS = config.report_interval_ms;
                SNAPSHOT_INTERVAL_MS = config.export.interval_ms;
                MAX_OUTPUT_IMG_SIZE = config.max_image_size;
                PATH_MASK = config.path_mask;
                PATH_OUTPUT_DAT = config.path_raw;
                PATH_OUTPUT_IMG = config.path_out;
                EXPORT_RAW = config.export.raw;
                EXPORT_PNG = config.export.png;
                THRESHOLD__B_G = config.threshold_g;
                THRESHOLD__G_R = config.threshold_r;

                Console.WriteLine("Settings loaded.");
            }
            catch
            {
                LoadDefaultSettings();

                Console.WriteLine("An error occured. The default settings have been loaded.");
            }
            finally
            {
                _progress = new double[THREADS];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void InitializeMaskAndImage()
        {
            _image = new BigFuckingAllocator<(int, int, int)>(IMG_WIDTH * IMG_HEIGHT);
            _image.AggressivelyClaimAllTheFuckingMemory();

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
        private static void SaveSnapshot()
        {
            if (EXPORT_RAW)
                SaveRaw();

            if (EXPORT_PNG)
                SaveTiledPNG();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SaveTiledPNG()
        {
            // TODO : improve this whole method with AVX2-instructions.

            int tiles = 1;

            while (IMG_WIDTH * IMG_HEIGHT / (ulong)(tiles * tiles) > (ulong)MAX_OUTPUT_IMG_SIZE)
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

            Bitmap[,] bmp = new ImageTiler<(int r, int g, int b)>(_image, pixel =>
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
                    string path = string.Format(PATH_OUTPUT_IMG, x, y);

                    bmp[x, y].Save(path, ImageFormat.Png);

                    _queue_rendered.Enqueue((path, true));
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SaveRaw()
        {
            _queue_rendered.Enqueue((PATH_OUTPUT_DAT, false));

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

            _queue_rendered.Enqueue((PATH_OUTPUT_DAT, true));
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
            (double left, double top, double right, double bottom) = IMAGE_BOUNDS;
            (double mleft, double mtop, double mright, double mbottom) = MASK_BOUNDS;

            for (int px_rel = 0; px_rel < xsteps; ++px_rel)
            {
                int px = px_rel + rank * x_px_per_thread;
                int px_mask = (int)((double)px / w * w_mask);

                for (int x_dpp = 0; x_dpp < DPP; ++x_dpp)
                {
                    double re = (right - left) * (px * DPP + x_dpp) / w / DPP + left;

                    for (int py = 0; py < (int)IMG_HEIGHT; ++py)
                    {
                        int py_mask = (int)((double)py / IMG_HEIGHT * h_mask);

                        for (int y_dpp = 0; y_dpp < DPP; ++y_dpp)
                        {
                            double im = (bottom - top) * (py * DPP + y_dpp) / IMG_HEIGHT / DPP + top;
                            bool check = re < mleft || re > mright || im < mbottom || im > mtop;

                            if (!check)
                            {
                                // TODO : _mask[px_mask, py_mask]
                            }

                            check = true;

                            if (check)
                                Calculate(in left, in top, in right, in bottom, new Complex(re, im));
                        }
                    }
                }

                _progress[rank] = (double)px_rel / xsteps;
            }

            _progress[rank] = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Calculate(in double left, in double top, in double right, in double bottom, Complex c)
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
                double x_idx = (q.Real - left) * IMG_WIDTH / (right - left);
                double y_idx = (q.Imaginary - top) * IMG_HEIGHT / (bottom - top);

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
