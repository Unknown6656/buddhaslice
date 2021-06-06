#define COMPLETE_BUDDHA

#nullable enable

using System.Runtime.Intrinsics.X86;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private static bool EXPORT_RAW_AT_END;
        private static bool EXPORT_RAW;
        private static bool EXPORT_PNG;
        private static bool CLAIM_MEM;
        private static int THRESHOLD__B_G;
        private static int THRESHOLD__G_R;
        private static (double left, double top, double right, double bottom) MASK_BOUNDS;
        private static (double left, double top, double right, double bottom) IMAGE_BOUNDS;

        private static ConcurrentQueue<(string name, bool finished)> _queue_rendered = new ConcurrentQueue<(string, bool)>();
        private static ExecutionDataflowBlockOptions? _options;

        // indexing: [y * WIDTH + x]
#if COMPLETE_BUDDHA
        private static BigFuckingAllocator<(bool Computed, int Iterations)> _image;
        private static Complex[,] _orbits;
        private static int[] _orbit_indices;
#else
        private static BigFuckingAllocator<(bool Computed, int Iterations_R, int Iterations_G, int Iterations_B)> _image;
#endif
        private static double[] _progress;
        private static bool[,] _mask;
        private static bool _isrunning = true;

        #endregion
        #region ENTRY POINT / SCHEDULING / ...

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

                SaveSnapshot(true);
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
#if COMPLETE_BUDDHA
            _orbits = new Complex[CORES, MAX_ITER];
            _orbit_indices = Enumerable.Repeat(-1, CORES).ToArray();
#endif
            ActionBlock<int> block = new ActionBlock<int>(Render, _options = new ExecutionDataflowBlockOptions
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
            const int WIDTH = 180;

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
            int left_progress = WIDTH / 2 + 2;

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($@"  RENDER CONFIGURATION:
       WIDTH:             {IMG_WIDTH:N0} px
       HEIGHT:            {IMG_HEIGHT:N0} px
       TOTAL PIXELS:      {IMG_HEIGHT * IMG_WIDTH:N0} px
       TOTAL COMPLEX OPS: {IMG_HEIGHT * IMG_WIDTH * (ulong)(DPP * DPP * (MAX_ITER + SLICE_LEVEL)):N0}
       ITERATIONS:        {MAX_ITER:N0}
       THREADS:           {THREADS}
       PIXELS PER THREAD: {IMG_HEIGHT * IMG_WIDTH / (ulong)THREADS:N0} px
       CORES:             {CORES} (+ 1)
       DPP:               {DPP}
       SLICE LEVEL:       {SLICE_LEVEL}
       SNAPSHOT INTERVAL: {SNAPSHOT_INTERVAL_MS / 1000d}s
       MAX. IMAGE SIZE:   {MAX_OUTPUT_IMG_SIZE / 1024d:N2} kpx
       MASK PATH:         ""{PATH_MASK}""
       OUTPUT PNG PATH:   ""{PATH_OUTPUT_IMG}""
       OUTPUT RAW PATH:   ""{PATH_OUTPUT_DAT}""
       CLAIM MEMORY:      {CLAIM_MEM}
       EXPORT PNG:        {EXPORT_PNG}
       EXPORT RAW:        {EXPORT_RAW}
       EXPORT RAW AT END: {EXPORT_RAW_AT_END}
       THRESHOLD B -> G:  {THRESHOLD__B_G}
       THRESHOLD G -> R:  {THRESHOLD__G_R}");

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
            Console.WriteLine("  THREADS:");

            int top_threads = Console.CursorTop;
            int t_per_row = (WIDTH - 12) / 7;
            (int l, int t) get_thread_pos(int rank) => (6 + (rank % t_per_row) * 7, top_threads + rank / t_per_row);

            Console.CursorTop = get_thread_pos(THREADS + 1).t + 1;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(new string('═', WIDTH));
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  EXPORTED DATA:");

            int top_exports = Console.CursorTop;

            Stopwatch sw_total = new Stopwatch();
            Stopwatch sw_report = new Stopwatch();
            Stopwatch sw_save = new Stopwatch();

            sw_total.Start();
            sw_report.Start();
            sw_save.Start();

            long max_mem = 0;
            bool fin = false;
            double oldp = 0;
            int updatememory = 0;

            do
                if (sw_save.ElapsedMilliseconds >= SNAPSHOT_INTERVAL_MS)
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
                        SaveSnapshot(false);

                        sw_save.Restart();
                    }); // we do NOT want to await this task!
                }
                else if (sw_report.ElapsedMilliseconds >= REPORTER_INTERVAL_MS)
                {
                    TimeSpan elapsed = sw_total.Elapsed;
                    double progr = _progress.Sum() / THREADS;
                    string est_rem = progr < 1e-5 ? "∞" : $"{TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / progr) - elapsed:dd':'hh':'mm':'ss}.00";

                    Console.CursorLeft = left_progress;
                    Console.CursorTop = top_progress;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine($"CURRENT TIME:                  {DateTime.Now:HH:mm:ss.ff}");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"ELAPSED TIME:               {elapsed:dd':'hh':'mm':'ss'.'ff}");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"REMAINING TIME (ESTIMATED): {est_rem}");

                    if (updatememory == 0)
                    {
                        long mem = Process.GetCurrentProcess().WorkingSet64;

                        max_mem = Math.Max(mem, max_mem);

                        Console.CursorLeft = left_progress;
                        Console.WriteLine($"CURRENT MEMORY FOOTPRINT:   {mem / 1048576d,11:N2} MB");
                        Console.CursorLeft = left_progress;
                        Console.WriteLine($"MAXIMUM MEMORY FOOTPRINT:   {max_mem / 1048576d,11:N2} MB");
                    }
                    else
                        Console.CursorTop += 2;

                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"IMAGE SIZE:                 {_image.BinarySize / 1048576d,11:N2} MB");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"TOTAL PROGRESS:             {progr * 100,11:N5} %");
                    Console.CursorLeft = left_progress;
                    Console.WriteLine($"CURRENT SPEED:              {(progr - oldp) * IMG_HEIGHT * IMG_WIDTH / (sw_report.ElapsedMilliseconds / 1000d),11:N0} px/s");

                    for (int i = 0; i < THREADS; ++i)
                    {
                        (Console.CursorLeft, Console.CursorTop) = get_thread_pos(i);
                        double p = Math.Round(_progress[i] * 100, 2);

                        if (p == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write(" 0.00%");
                        }
                        else if (p < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write($"{p,5:N2}%");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.Write("100.0%");
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

                    if (!fin && progr == 1)
                    {
                        Console.CursorLeft = 6;
                        Console.CursorTop = top_exports;
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write("Finished rendering. Saving final result ...");

                        ++top_exports;

                        fin = true;
                    }

                    updatememory = (updatememory + 1) % 3;
                    oldp = progr;

                    sw_report.Restart();
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
                nameof(SaveTiledPNG),
                nameof(CalculateTiles)
            })
                RuntimeHelpers.PrepareMethod(t.GetMethod(m, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.MethodHandle);
        }

#endregion
        #region LOAD / SAVE

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

            CLAIM_MEM = true;
            EXPORT_RAW = true;
            EXPORT_RAW_AT_END = true;
            EXPORT_PNG = true;

            THRESHOLD__B_G = 40;
            THRESHOLD__G_R = 300;
        }

        private static void LoadSettings()
        {
            Console.ForegroundColor = ConsoleColor.White;
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
                CLAIM_MEM = config.claim_memory;
                EXPORT_RAW_AT_END = config.export.raw_at_end;
                EXPORT_RAW = config.export.raw;
                EXPORT_PNG = config.export.png;
                THRESHOLD__B_G = config.threshold_g;
                THRESHOLD__G_R = config.threshold_r;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Settings loaded.");
            }
            catch
            {
                LoadDefaultSettings();

                Console.ForegroundColor = ConsoleColor.Red;
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
#if COMPLETE_BUDDHA
            _image = new BigFuckingAllocator<(bool, int)>(IMG_WIDTH * IMG_HEIGHT);
#else
            _image = new BigFuckingAllocator<(bool, int, int, int)>(IMG_WIDTH * IMG_HEIGHT);
#endif
            if (CLAIM_MEM)
                _image.AggressivelyClaimAllTheFuckingMemory();

            // TODO : do something if mask has not been found.

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
        private static void SaveSnapshot(bool final)
        {
            if (final ? EXPORT_RAW_AT_END : EXPORT_RAW)
                SaveRaw();

            if (final || EXPORT_PNG)
                SaveTiledPNG();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe Bitmap[,] CalculateTiles(int tilecount)
        {
            // TODO : improve this whole method with AVX2-instructions.

            int max(params int[] v) => v.Max();
            uint clamp(double v) => (uint)Math.Max(0, Math.Min(v, 255));
            double brightest = 0;

            for (ulong i = 0; i < IMG_WIDTH * IMG_HEIGHT; ++i)
            {
                var pixel = _image[i];
#if COMPLETE_BUDDHA
                brightest = Math.Max(brightest, pixel->Iterations);
#else
                brightest = Math.Max(brightest, max(pixel->Iterations_B, pixel->Iterations_G, pixel->Iterations_R));
#endif
            }

#if COMPLETE_BUDDHA
            brightest = 512 / Math.Sqrt(brightest);
#else
            brightest = 6000 / Math.Sqrt(brightest);
#endif

#if COMPLETE_BUDDHA
            return new ImageTiler<(bool v, int i)>(_image, pixel =>
            {
                if (!pixel.v)
                    return 0x00000000u;

                uint v = clamp(Math.Sqrt(pixel.i) * brightest);

                return 0xff000000u
                     | (v << 16)
                     | (v << 8)
                     | v;
            }).GenerateTiles((tilecount, tilecount), ((int)IMG_WIDTH, (int)IMG_HEIGHT));
#else
            return new ImageTiler<(bool v, int r, int g, int b)>(_image, pixel =>
            {
                if (!pixel.v)
                    return 0x00000000u;

                double r = Math.Sqrt(pixel.r) * brightest;
                double g = Math.Sqrt(pixel.g) * brightest;
                double b = Math.Sqrt(pixel.b) * brightest;

                return 0xff000000u
                     | (clamp(r) << 16)
                     | (clamp(g) << 8)
                     | clamp(b);
            }).GenerateTiles((tilecount, tilecount), ((int)IMG_WIDTH, (int)IMG_HEIGHT));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SaveTiledPNG()
        {
            int tiles = 1;

            while (IMG_WIDTH * IMG_HEIGHT / (ulong)(tiles * tiles) > (ulong)MAX_OUTPUT_IMG_SIZE)
                ++tiles;

            Bitmap[,] bmp = CalculateTiles(tiles);

            for (int x = 0; x < tiles; ++x)
                for (int y = 0; y < tiles; ++y)
                {
                    string path = string.Format(PATH_OUTPUT_IMG, x, y);
                    using Bitmap b = bmp[x, y];

                    SetExifData(b, 0x0131, "Buddhaslice by Unknown6656");
                    SetExifData(b, 0x013b, "Unknown6656");
                    SetExifData(b, 0x8298, "(c) 2019, Unknown6656");
                    SetExifData(b, 0x010e, $@"
WIDTH:             {IMG_WIDTH:N0} px
HEIGHT:            {IMG_HEIGHT:N0} px
ITERATIONS:        {MAX_ITER:N0}
THREADS:           {THREADS}
DPP:               {DPP}
SLICE LEVEL:       {SLICE_LEVEL}
TILE (X, Y):       ({x}, {y})
MASK PATH:         ""{PATH_MASK}""
THRESHOLD B -> G:  {THRESHOLD__B_G}
THRESHOLD G -> R:  {THRESHOLD__G_R}
".Trim());

                    b.Save(path, ImageFormat.Png);

                    _queue_rendered.Enqueue((path, true));
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetExifData(Bitmap bmp, int id, string value)
        {
            PropertyItem prop = (PropertyItem)FormatterServices.GetUninitializedObject(typeof(PropertyItem));
            byte[] bytes = Encoding.ASCII.GetBytes(value + '\0');

            prop.Id = id;
            prop.Type = 2;
            prop.Value = bytes;
            prop.Len = bytes.Length;

            bmp.SetPropertyItem(prop);
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
                wr.Write(_image[i]->Computed);
#if COMPLETE_BUDDHA
                wr.Write(_image[i]->Iterations);
#else
                wr.Write(_image[i]->Iterations_R);
                wr.Write(_image[i]->Iterations_G);
                wr.Write(_image[i]->Iterations_B);
#endif
            }

            _queue_rendered.Enqueue((PATH_OUTPUT_DAT, true));
        }

        #endregion
        #region RENDER / CALCULATION METHODS

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Render(int rank)
        {
#if COMPLETE_BUDDHA
            int orbit_idx = 0;

            while (_orbit_indices[orbit_idx] >= 0)
                orbit_idx = (orbit_idx + 1) % CORES;
#endif
            int w = (int)IMG_WIDTH;
            int x_px_per_thread = w / THREADS;
            int xsteps = rank < THREADS - 1 ? x_px_per_thread : w - x_px_per_thread * (THREADS - 1);
            (double left, double top, double right, double bottom) = IMAGE_BOUNDS;
            (double mleft, double mtop, double mright, double mbottom) = MASK_BOUNDS;
            (int mwidth, int mheight) = (_mask.GetLength(0), _mask.GetLength(1));

            for (int px_rel = 0; px_rel < xsteps; ++px_rel)
            {
                int px = px_rel + rank * x_px_per_thread;

                for (int x_dpp = 0; x_dpp < DPP; ++x_dpp)
                {
                    double re = (right - left) * (px * DPP + x_dpp) / w / DPP + left;

                    for (int py = 0; py < (int)IMG_HEIGHT; ++py)
                    {
                        for (int y_dpp = 0; y_dpp < DPP; ++y_dpp)
                        {
                            double im = (bottom - top) * (py * DPP + y_dpp) / IMG_HEIGHT / DPP + top;
                            bool check = mleft <= re && re <= mright && mtop <= im && im <= mbottom;

                            if (check)
                            {
                                int px_mask = (int)((re - mleft) / (mright - mleft) * mwidth);
                                int py_mask = (int)((im - mtop) / (mbottom - mtop) * mheight);

                                check = _mask[px_mask, py_mask];
                            }
                            else
                                check = true;

                            if (check)
                                Calculate(in left, in top, in right, in bottom, new Complex(re, im), orbit_idx);
                        }

                        _progress[rank] = (px_rel + (x_dpp + (py + 1d) / IMG_HEIGHT) / DPP) / xsteps;
                        _image[(ulong)py * IMG_WIDTH + (ulong)px]->Computed = true;
                    }
                }
            }

            _orbit_indices[orbit_idx] = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Calculate(in double left, in double top, in double right, in double bottom, Complex c
#if COMPLETE_BUDDHA
            , int orbit_idx
#endif
            )
        {
            int count = 0;
            Complex z = 0; // 0 ?

            while (Math.Abs(z.Imaginary) < 2 && Math.Abs(z.Imaginary) < 2 && count < MAX_ITER)
                _orbits[orbit_idx, count++] = z = z * z + c;

            for (int i = 0; i < count; ++i)
            {
                Complex q = _orbits[orbit_idx, i];
                double x_idx = (q.Real - left) * IMG_WIDTH / (right - left);
                double y_idx = (q.Imaginary - top) * IMG_HEIGHT / (bottom - top);

                if (x_idx >= 0 && x_idx < IMG_WIDTH &&
                    y_idx >= 0 && y_idx < IMG_HEIGHT)
                {
                    ulong idx = (ulong)y_idx * IMG_WIDTH + (ulong)x_idx;
#if COMPLETE_BUDDHA
                    ++_image[idx]->Iterations;
#else
                    if (i > THRESHOLD__G_R + THRESHOLD__B_G)
                        _image[idx]->Iterations_R += i - THRESHOLD__G_R - THRESHOLD__B_G;

                    if (i > THRESHOLD__B_G)
                        _image[idx]->Iterations_G += (short)(Math.Min(i, THRESHOLD__G_R) - THRESHOLD__B_G);

                    _image[idx]->Iterations_B += (short)Math.Max(0, Math.Min(i - 5, THRESHOLD__B_G));
#endif
                }
            }
        }

#endregion
    }
}
