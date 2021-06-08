// #define COMPLETE_BUDDHA

using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;
using System.Drawing.Imaging;
using System.Drawing;
using System.Numerics;
using System.Text.Json;
using System.Text;
using System.Linq;
using System.IO;
using System;

using Unknown6656.BigFuckingAllocator;
using Unknown6656.Controls.Console;
using Unknown6656.IO;
using Unknown6656.Common;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;


#if DOUBLE_PRECISION
using precision = System.Double;
#else
using precision = System.Single;
#endif


namespace buddhaslice
{
    public struct PIXEL
    {
        public bool Computed;
        public ushort R, G, B;
#if COMPLETE_BUDDHA
        public int Iterations;
#endif

        public override string ToString() => $"{Computed}| {R} {G} {B}";
    }

    public static class Program
    {
        public const string PATH_CONFIG = "settings.json";

        public static Settings Settings { get; private set; }

        private static ConcurrentQueue<(string name, bool finished)> _queue_rendered = new();
        private static ExecutionDataflowBlockOptions? _options;

        // indexing: [y * WIDTH + x]
        private static BigFuckingAllocator<PIXEL> _image;
#if COMPLETE_BUDDHA
        private static Complex[,] _orbits;
        private static int[] _orbit_indices;
#endif
        private static precision[] _progress;
        private static bool[,] _mask;
        private static bool _isrunning = true;


        #region ENTRY POINT / SCHEDULING / ...

        public static void Main(string[] _)
        {
            void exit()
            {
                _isrunning = false;
                _image?.Dispose();

                Console.CursorVisible = true;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.BackgroundColor = ConsoleColor.Black;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, e) => exit();

            try
            {
                LoadSettings();

                Task _reporter = Task.Factory.StartNew(ProgressReporterTask);

                InitializeMaskAndImage();
                WarmUpMethods();
                RenderImage();

                unsafe
                {
                    for (ulong i = 0; i < _image.ItemCount; ++i)
                        if (!_image[i]->Computed)
                            Console.WriteLine($"{i}: {*_image[i]}");
                }

                SaveSnapshot(true);

                _isrunning = false;
                _reporter.GetAwaiter().GetResult();
            }
            catch (Exception? ex)
            when (!Debugger.IsAttached)
            {
                _isrunning = false;

                StringBuilder sb = new();

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
       WIDTH:             {Settings.width:N0} px
       HEIGHT:            {Settings.height:N0} px
       TOTAL PIXELS:      {Settings.height * Settings.width:N0} px
       TOTAL COMPLEX OPS: {Settings.height * Settings.width * (ulong)(Settings.dpp * Settings.dpp * (Settings.max_iter + Settings.slice_offset) * Settings.slice_count):N0}
       ITERATIONS:        {Settings.max_iter:N0}
       PIXELS PER THREAD: {Settings.height * Settings.width / (ulong)Settings.cores:N0} px
       THREADS:           {Settings.cores} (+ 1)
       DPP:               {Settings.dpp}
       SLICE LEVELS:      {Settings.slice_offset}...{Settings.slice_offset + Settings.slice_count}
       SNAPSHOT INTERVAL: {Settings.export.interval_ms / 1000d}s
       MAX. IMAGE SIZE:   {Settings.export.max_image_size / 1024d:N2} kpx
       MASK PATH:         ""{Settings.mask.path}""
       OUTPUT PNG PATH:   ""{Settings.export.path_png}""
       OUTPUT RAW PATH:   ""{Settings.export.path_raw}""
       EXPORT PNG:        {Settings.export.png}
       EXPORT RAW:        {Settings.export.raw}
       EXPORT RAW AT END: {Settings.export.raw_at_end}
       THRESHOLD B -> G:  {Settings.threshold_g}
       THRESHOLD G -> R:  {Settings.threshold_r}");

            int tmp = Console.CursorTop;

            Console.CursorLeft = WIDTH / 2 - 1;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.CursorTop = top_progress - 1;
            Console.Write('╦');
            Console.CursorLeft--;

            for (int t = top_progress; t < tmp; ++t)
            {
                Console.CursorTop = t;
                Console.Write('║');
                Console.CursorLeft--;
            }

            ++Console.CursorTop;
            Console.Write('╩');

            ++top_progress;

            Console.CursorTop = tmp;
            Console.CursorLeft = 0;
            Console.WriteLine(new string('═', WIDTH));
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  THREADS:");

            int top_threads = Console.CursorTop;
            int t_per_row = (WIDTH - 12) / 14;
            (int l, int t) get_thread_pos(int rank) => (6 + (rank % t_per_row) * 13, top_threads + rank / t_per_row);

            Console.CursorTop = get_thread_pos(Settings.cores + 1).t + 1;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(new string('═', WIDTH));
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  EXPORTED DATA:");

            int top_exports = Console.CursorTop;

            Stopwatch sw_total = new();
            Stopwatch sw_report = new();
            Stopwatch sw_save = new();

            sw_total.Start();
            sw_report.Start();
            sw_save.Start();

            long max_mem = 0;
            bool fin = false;
            precision oldp = 0;
            int updatememory = 0;

            do
                if (sw_save.ElapsedMilliseconds >= Settings.export.interval_ms)
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
                else if (sw_report.ElapsedMilliseconds >= Settings.report_interval_ms)
                {
                    TimeSpan elapsed = sw_total.Elapsed;
                    precision progr = _progress.Sum() / Settings.cores;
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
                    Console.WriteLine($"CURRENT SPEED:              {(progr - oldp) * Settings.height * Settings.width / (sw_report.ElapsedMilliseconds / 1000d),11:N0} px/s");

                    for (int i = 0; i < Settings.cores; ++i)
                    {
                        (Console.CursorLeft, Console.CursorTop) = get_thread_pos(i);
                        precision p = (precision)Math.Round(_progress[i] * 100, 8);

                        if (p == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write(" 0.00000000%");
                        }
                        else if (p < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write($"{p,11:N8}%");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.Write("100.0000000%");
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
                    await Task.Delay(Settings.report_interval_ms / 3);
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
                nameof(RenderImage),
                nameof(Calculate),
                nameof(SaveTiledPNG),
                nameof(DivideImageIntoTiles)
            })
                RuntimeHelpers.PrepareMethod(t.GetMethod(m, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.MethodHandle);
        }

        #endregion
        #region LOAD / SAVE

        private static void LoadSettings()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Loading settings...");

            try
            {
                Settings = DataStream.FromFile(PATH_CONFIG).ToJSON<Settings>();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Settings loaded.");
            }
            catch
            {
                Bounds default_bounds = new(-2f, -1.1f, .75f, 1.1f);

                Settings = new()
                {
                    mask = new()
                    {
                        bounds = default_bounds,
                        path = "mask.png",
                    },
                    bounds = default_bounds,
#if DEBUG
                    width = 3840,
                    height = 2160,
                    max_iter = 10000,
                    cores = 8,
                    dpp = 3,
#else
                    width = 28_800,
                    height = 16_200,
                    max_iter = 50_000,
                    cores = 8,
                    dpp = 16,
#endif
                    slice_offset = 8,
                    slice_count = 1,
                    report_interval_ms = 500,
                    threshold_g = 40,
                    threshold_r = 300,
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
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("An error occured. The default settings have been loaded.");
            }
            finally
            {
                _progress = new precision[Settings.cores];
            }
        }

        private static unsafe void InitializeMaskAndImage()
        {
            _image = new(Settings.width * Settings.height);

            try
            {
                using Bitmap mask = (Bitmap)Image.FromFile(Settings.mask.path);
                int w = mask.Width;
                int h = mask.Height;

                _mask = new bool[w, h];

                BitmapData dat = mask.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
                byte* ptr = (byte*)dat.Scan0;

                Parallel.For(0, w * h, i => _mask[i % w, i / w] = ((double)ptr[i * 3 + 0] + ptr[i * 3 + 1] + ptr[i * 3 + 2]) > 0);

                mask.UnlockBits(dat);
            }
            catch
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"The mask could not be loaded from '{Settings.mask.path}'.");

                _mask = new bool[1, 1] { { true } }; // always compute
            }
        }

        private static void SaveSnapshot(bool final)
        {
            if (final ? Settings.export.raw_at_end : Settings.export.raw)
                SaveRaw();

            if (final || Settings.export.png)
                SaveTiledPNG();
        }

        private static void SaveTiledPNG()
        {
            int tiles = 1;

            while (Settings.width * Settings.height / (ulong)(tiles * tiles) > (ulong)Settings.export.max_image_size)
                ++tiles;

            Bitmap[,] bmp = DivideImageIntoTiles(tiles);

            for (int x = 0; x < tiles; ++x)
                for (int y = 0; y < tiles; ++y)
                {
                    string path = string.Format(Settings.export.path_png, x, y);
                    using Bitmap b = bmp[x, y];

                    SetExifData(b, 0x0131, "Buddhaslice by Unknown6656");
                    SetExifData(b, 0x013b, "Unknown6656");
                    SetExifData(b, 0x8298, "Copyright (c) 2019, Unknown6656");
                    SetExifData(b, 0x010e, $@"
WIDTH:             {Settings.width:N0} px
HEIGHT:            {Settings.height:N0} px
ITERATIONS:        {Settings.max_iter:N0}
CORES:             {Settings.cores}
DPP:               {Settings.dpp}
SLICE LEVELS:      {Settings.slice_offset}...{Settings.slice_offset + Settings.slice_count}
TILE (X, Y):       ({x}, {y})
TILE WIDTH:        {b.Width}
TILE HEIGHT:       {b.Height}
MASK PATH:         ""{Settings.mask.path}""
THRESHOLD B -> G:  {Settings.threshold_r}
THRESHOLD G -> R:  {Settings.threshold_g}
".Trim());

                    b.Save(path, ImageFormat.Png);

                    _queue_rendered.Enqueue((path, true));
                }
        }

        private static unsafe Bitmap[,] DivideImageIntoTiles(int tilecount)
        {
            static uint PixelTranslator(PIXEL* pixel, precision brightest)
            {
                static uint clamp(double value) => value < 0 ? 0u : value > 255 ? 255u : (uint)value;
                double r = Math.Sqrt(pixel->R) * brightest;
                double g = Math.Sqrt(pixel->G) * brightest;
                double b = Math.Sqrt(pixel->B) * brightest;

                return 0xff000000u
                     | (clamp(r) << 16)
                     | (clamp(g) << 8)
                     | clamp(b);
            }
            ImageTiler<precision> tiler = new(_image, &PixelTranslator);
            precision brightest = 0;

            for (ulong i = 0, count = Settings.width * Settings.height; i < count; ++i)
            {
                PIXEL* pixel = _image[i];

#if COMPLETE_BUDDHA
                (pixel->R, pixel->G, pixel->B) = IterationToRGB(pixel->Iterations);
#endif
                brightest = Math.Max(brightest, pixel->R);
                brightest = Math.Max(brightest, pixel->G);
                brightest = Math.Max(brightest, pixel->B);
            }

            //brightest = (precision)(6000 / Math.Sqrt(brightest));
            brightest = (precision)(512 / Math.Sqrt(brightest));

            return tiler.GenerateTiles(tilecount, tilecount, (int)Settings.width, (int)Settings.height, brightest);
        }

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

        private static unsafe void SaveRaw()
        {
            _queue_rendered.Enqueue((Settings.export.path_raw, false));

            using FileStream fs = new(Settings.export.path_raw, FileMode.Create, FileAccess.Write, FileShare.Read);
            using BinaryWriter wr = new(fs);

            wr.Write(Settings.width);
            wr.Write(Settings.height);

            for (ulong i = 0, size = Settings.width * Settings.height; i < size; ++i)
            {
                PIXEL* pixel = _image[i];

                wr.Write(pixel->Computed);
#if COMPLETE_BUDDHA
                wr.Write(pixel->Iterations);
#else
                wr.Write(pixel->R);
                wr.Write(pixel->G);
                wr.Write(pixel->B);
#endif
            }

            wr.Flush();
            fs.Flush();
            fs.Close();

            _queue_rendered.Enqueue((Settings.export.path_raw, true));
        }

        #endregion
        #region RENDER / CALCULATION METHODS

        private static void RenderImage()
        {
#if COMPLETE_BUDDHA
            _orbits = new Complex[Settings.cores, Settings.max_iter];
            _orbit_indices = Enumerable.Repeat(-1, Settings.cores).ToArray();
#endif


#if COMPLETE_BUDDHA
            int orbit_index = 0;

            while (_orbit_indices[orbit_index] >= 0)
                orbit_index = (orbit_index + 1) % Settings.cores;
#endif


            Bounds i_bounds = Settings.bounds;
            Bounds m_bounds = Settings.mask.bounds;
            (int mask_width, int mask_height) = (_mask.GetLength(0), _mask.GetLength(1));
            ulong cores = (ulong)Settings.cores;

            ActionBlock<ulong> block = new(core =>
            {
                for (ulong index = core, total = _image.ItemCount; index < total; index += cores)
                    unsafe
                    {
                        ulong px = index % Settings.width;
                        ulong py = index / Settings.width;


                        for (int x_dpp = 0; x_dpp < Settings.dpp; ++x_dpp)
                        {
                            precision re = (px * i_bounds.Width * Settings.dpp + x_dpp) / ((precision)Settings.width * Settings.dpp) + i_bounds.left;

                            for (int y_dpp = 0; y_dpp < Settings.dpp; ++y_dpp)
                            {
                                precision im = (py * i_bounds.Height * Settings.dpp + y_dpp) / ((precision)Settings.height * Settings.dpp) + i_bounds.top;
                                bool compute = true;

                                if (m_bounds.Contains(re, im))
                                {
                                    int px_mask = (int)((re - m_bounds.left) / m_bounds.Width * mask_width);
                                    int py_mask = (int)((im - m_bounds.top) / m_bounds.Height * mask_height);

                                    compute = _mask[px_mask, py_mask];
                                }

                                if (compute)
#if COMPLETE_BUDDHA
                                    Calculate(new Complex(re, im), in i_bounds, orbit_index);
#else
                                    Calculate(new Complex(re, im), in i_bounds);
#endif
                            }

                            _progress[core] = (index * (precision)Settings.dpp + x_dpp) / (total * (precision)Settings.dpp);
                        }

                        _image[index]->Computed = true;
                    }

                _progress[core] = 1;
            }, new ExecutionDataflowBlockOptions(){ MaxDegreeOfParallelism = Environment.ProcessorCount });

            for (ulong core = 0; core < cores; ++core)
                block.Post(core);

            block.Complete();
            block.Completion.Wait();

#if COMPLETE_BUDDHA
            _orbit_indices[orbit_index] = -1;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (ushort R, ushort G, ushort B) IterationToRGB(int iterations)
        {
            ushort r = 0, g = 0, b = (ushort)Math.Max(0, Math.Min(iterations - 5, Settings.threshold_g));

            if (iterations > Settings.threshold_r + Settings.threshold_g)
                r = (ushort)Math.Min(iterations - Settings.threshold_r - Settings.threshold_g, ushort.MaxValue);

            if (iterations > Settings.threshold_g)
                g = (ushort)(Math.Min(iterations, Settings.threshold_r) - Settings.threshold_g);

            return (r, g, b);
        }

#if COMPLETE_BUDDHA
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Calculate(Complex c, in Bounds bounds, int orbit_index)
        {
            int iteration_count = 0;
            Complex z = 0;

            while (Math.Abs(z.Imaginary) < 2 && Math.Abs(z.Imaginary) < 2 && iteration_count < Settings.max_iter)
                _orbits[orbit_index, iteration_count++] = z = (z * z) + c;

            for (int i = 0; i < iteration_count; ++i)
            {
                Complex q = _orbits[orbit_index, i];
                ulong x_index = (ulong)((q.Real - Settings.bounds.image.left) * Settings.width / Settings.bounds.image.Width);
                ulong y_index = (ulong)((q.Imaginary - Settings.bounds.image.top) * Settings.height / Settings.bounds.image.Height);

                if (x_index >= 0 && x_index < Settings.width && y_index >= 0 && y_index < Settings.height)
                    ++_image[(y_index * Settings.width) + x_index]->Iterations;
            }
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Calculate(Complex c, in Bounds bounds)
        {
            int iteration_count = Settings.slice_offset;
            Complex z = 0;
            Complex q;

            for (int i = 0; i < iteration_count; ++i)
                z = z * z + c;

            q = z;

            while (Math.Abs(z.Imaginary) < 2 && Math.Abs(z.Imaginary) < 2 && iteration_count < Settings.max_iter)
            {
                z = z * z + c;
                ++iteration_count;
            }

            if (iteration_count < Settings.max_iter)
            {
                ulong x_index = (ulong)((q.Real - bounds.left) * Settings.width / bounds.Width);
                ulong y_index = (ulong)((q.Imaginary - bounds.top) * Settings.height / bounds.Height);
                ulong index = y_index * Settings.width + x_index;

                if (x_index >= 0 && x_index < Settings.width && y_index >= 0 && y_index < Settings.height)
                {
                    (ushort R, ushort G, ushort B) = IterationToRGB(iteration_count);
                    PIXEL* pixel = _image[index];

                    pixel->R += R;
                    pixel->G += G;
                    pixel->B += B;
                }
            }
        }
#endif

        #endregion
    }
}
