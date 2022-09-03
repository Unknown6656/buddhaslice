//#define COMPLETE_BUDDHA
//#define DOUBLE_PRECISION


#if DOUBLE_PRECISION
global using precision = System.Double;
#else
global using precision = System.Single;
#endif

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using Unknown6656.Imaging.Effects;
using Unknown6656.Imaging;
using Unknown6656.Common;
using Unknown6656.IO;

using ColorMap = Unknown6656.Imaging.ColorMap;

namespace buddhaslice;


public struct PIXEL
{
    public bool Computed;
    public volatile int Iterations;

    public override string ToString() => $"{Computed}|{Iterations}";
}

public static class Program
{
    public const string PATH_CONFIG = "settings.json";
    public const string PATH_NATIVE = "native.dll";

    public static Settings Settings { get; private set; }

    private static ConcurrentQueue<(string name, bool finished)> _queue_rendered = new();
    private static ExecutionDataflowBlockOptions? _options;

    // indexing: [y * WIDTH + x]
    private static BigFuckingAllocator<PIXEL> _image;
    private static precision[] _progress;
    private static bool[,] _mask;
    private static bool _isrunning = true;


    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long TotalMemoryInKilobytes);

    [DllImport(PATH_NATIVE, EntryPoint = "render_image_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void RenderImageCore_Native(ref render_args args);


    #region ENTRY POINT / SCHEDULING / ...

    public static void Main(string[] _)
    {
        static void exit()
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
        Console.OutputEncoding = Encoding.UTF8;
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.BackgroundColor = ConsoleColor.Black;
        Console.Clear();
        Console.CursorVisible = false;
        Console.BufferWidth = Math.Max(WIDTH, Console.BufferWidth);
        Console.WindowWidth = Math.Max(WIDTH, Console.WindowWidth);
        Console.BufferHeight = Math.Max(WIDTH * 2, Console.BufferHeight);
        Console.WindowHeight = Math.Max(WIDTH / 3, Console.WindowHeight);
        Console.WriteLine(new string('═', WIDTH));

        int top_progress = Console.CursorTop;
        int left_progress = WIDTH / 2 + 2;
        const int ALIGN = 30;

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"""
            RENDER CONFIGURATION:
                WIDTH:                {Settings.width,ALIGN:N0} px
                HEIGHT:               {Settings.height,ALIGN:N0} px
                TOTAL PIXELS:         {Settings.height * Settings.width,ALIGN:N0} px
                TOTAL COMPLEX OPS:    {Settings.height * Settings.width * (ulong)(Settings.dpp * Settings.dpp * (Settings.max_iter + Settings.slice_offset) * Settings.slice_count),ALIGN:N0} c.ops
                COMPUTATION ENGINE:   {(Settings.native ? "native (C, MSVC)" : "managed (C#, .NET)"),ALIGN}
                FLOAT SIZE:           {sizeof(precision),ALIGN} B ({(sizeof(precision) == sizeof(float) ? "single" : "double")} precision)
                ITERATIONS:           {Settings.max_iter,ALIGN:N0}
                PIXELS PER THREAD:    {Settings.height * Settings.width / (ulong)Settings.batches,ALIGN:N0} px
                BATCHES:              {Settings.batches,ALIGN}
                THREADS:              {Environment.ProcessorCount - 1,ALIGN} (+ 1)
                DPP:                  {Settings.dpp,ALIGN}
                SLICE LEVELS:         {$"{Settings.slice_offset}...{Settings.slice_offset + Settings.slice_count}",ALIGN}
                SNAPSHOT INTERVAL:    {Settings.export.interval_ms / 1000d,ALIGN} s
                MAX. IMAGE SIZE:      {Settings.export.max_image_size,ALIGN:N0} px
                MASK PATH:            {'"' + Settings.mask.path,ALIGN}"
                OUTPUT PNG PATH:      {'"' + Settings.export.path_png,ALIGN}"
                OUTPUT RAW PATH:      {'"' + Settings.export.path_raw,ALIGN}"
                EXPORT PNG:           {Settings.export.png,ALIGN}
                EXPORT RAW:           {Settings.export.raw,ALIGN}
                EXPORT RAW AT END:    {Settings.export.raw_at_end,ALIGN}
                THRESHOLD B -> G:     {(Settings.grayscale ? "[mapped] " : "") + Settings.threshold_g,ALIGN}
                THRESHOLD G -> R:     {(Settings.grayscale ? "[mapped] " : "") + Settings.threshold_r,ALIGN}
                GRAYSCALE:            {(Settings.grayscale ? "[mapped] " : "") + Settings.grayscale,ALIGN}
                COLOR MAP:            {(Settings.grayscale ? "" : "[manual] ") + Settings.color_map,ALIGN}
        """);

        int bottom_progress = Console.CursorTop;

        Console.CursorLeft = WIDTH / 2 - 1;
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.CursorTop = top_progress - 1;

        ConsoleExtensions.WriteVertical('╦' + new string('║', bottom_progress - top_progress));

        (int progress_text_width, int system_counters_top) = ConsoleExtensions.WriteBlock("""
        CURRENT PROGRESS:
            START TIME:
            CURRENT TIME:
            ELAPSED TIME:
            REMAINING TIME (ESTIMATED):
            TOTAL PROGRESS:
            PROGRESS SPEED:
            IMAGE SIZE:
            PXIELS WRITTEN:
            CURRENT SPEED:

            CURRENT MEMORY FOOTPRINT:
            MAXIMUM MEMORY FOOTPRINT:
            FREE SYSTEM MEMORY:
            TOTAL SYSTEM MEMORY:
            CPU USAGE (GLOBAL):
        """, (left_progress, top_progress));

        top_progress += 2;
        left_progress += progress_text_width + 3;

        Console.CursorTop = top_progress - 1;
        Console.CursorLeft = left_progress;
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ff"));

        Console.CursorTop = bottom_progress;
        Console.CursorLeft = 0;
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(new string('═', WIDTH));
        Console.CursorLeft = WIDTH / 2 - 1;
        Console.WriteLine('╩');
        Console.WriteLine("  BATCHES / THREADS:");

        const int threads_per_row = 3;

        int top_threads = Console.CursorTop;
        int threads_b10 = (int)Math.Ceiling(Math.Log10(Settings.batches));
        int threads_label_sz = 4 + threads_b10;
        int threads_size = (WIDTH - 6) / threads_per_row - threads_label_sz;

        (int left, int top, int padding) get_thread_pos(int rank)
        {
            if (rank == Settings.batches)
                return (
                    get_thread_pos(0).left,
                    get_thread_pos(Settings.batches - 1).top + 1,
                    threads_per_row * (threads_size + 1) - threads_label_sz - 11
                );
            else
                return (
                    7 + threads_b10 + (rank % threads_per_row) * (threads_size + 3),
                    top_threads + rank / threads_per_row,
                    threads_size - threads_label_sz - 14
                );
        }

        Console.ForegroundColor = ConsoleColor.White;

        for (int i = 0; i <= Settings.batches; ++i)
        {
            (int left, int top, int padding) = get_thread_pos(i);

            Console.CursorLeft = left - 2 - threads_b10;
            Console.CursorTop = top;
            Console.Write($"{(i == Settings.batches ? "Σ" : (i + 1).ToString()).PadLeft(threads_b10)}:                {new string(' ', padding)}");
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine('\n' + new string('═', WIDTH));
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

        GetPhysicallyInstalledSystemMemory(out long memKb);

        precision perf_mem_glob = memKb / 1024f;
        using PerformanceCounter perf_mem_avail = new("Memory", "Available MBytes");
        using PerformanceCounter perf_cpu_glob = new("Processor", "% Processor Time", "_Total");
        using PerformanceCounter perf_cpu = new("Process", "% Processor Time", Process.GetCurrentProcess().ProcessName);

        do
            try
            {
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
                    precision progr = _progress.Sum() / Settings.batches;
                    string est_rem = progr < 1e-5 ? "∞" : $"{TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / progr) - elapsed:dd':'hh':'mm':'ss}.00";
                    double pixels_per_sec = (progr - oldp) * Settings.height * Settings.width * 1000d / sw_report.ElapsedMilliseconds;
                    long mem = Process.GetCurrentProcess().WorkingSet64;

                    max_mem = Math.Max(mem, max_mem);

                    Console.ForegroundColor = ConsoleColor.White;
                    ConsoleExtensions.WriteBlock($"""
                        {DateTime.Now:yyyy-MM-dd HH:mm:ss.ff}
                                {elapsed:dd':'hh':'mm':'ss'.'ff}
                                {est_rem}
                                   {progr * 100,11:N5} %
                                   {(progr - oldp) * 100_000d / sw_report.ElapsedMilliseconds,11:N5} %/s
                                   {_image.BinarySize / 1048576d,11:N2} MB
                                   {progr * Settings.height * Settings.width,11:N0} px
                                   {pixels_per_sec * sizeof(precision) / 341.333,11:N0} kB/s
                                   {pixels_per_sec,11:N0} px/s
                                   {mem / 1048576d,11:N2} MB  ({mem * 100d / max_mem,6:N2} %)
                                   {max_mem / 1048576d,11:N2} MB
                                   {perf_mem_avail.NextValue(),11:N2} MB  ({perf_mem_avail.NextValue() / perf_mem_glob * 100,6:N2} %)
                                   {perf_mem_glob,11:N2} MB
                                   {perf_cpu_glob.NextValue(),11:N2} %
                        """, (left_progress, top_progress));

                    for (int i = 0; i <= Settings.batches; ++i)
                    {
                        (int left, int top, int progress_total_width) = get_thread_pos(i);

                        Console.CursorLeft = left;
                        Console.CursorTop = top;
                        precision progress = i < Settings.batches ? _progress[i] : progr;
                        precision p = (precision)Math.Round(progress * 100, 8);
                        int progress_width = (int)Math.Round(progress * progress_total_width);

                        if (p == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write("  0.00000000 %");
                        }
                        else if (p < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write($" {p,11:N8} %");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.Write("100.00000000 %");
                        }

                        ++Console.CursorLeft;

                        if (progress_width > 0)
                            if (i < Settings.batches)
                                Console.Write(new string('━', progress_width));
                            else
                            {
                                int finished = (int)Math.Min(progress_width, Math.Round(_progress.Count(p => p >= 1) * progress_total_width / (precision)_progress.Length));

                                if (finished > 0)
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                                    Console.Write(new string('━', finished));
                                }

                                if (finished < progress_width)
                                {
                                    Console.ForegroundColor = ConsoleColor.Yellow;
                                    Console.Write(new string('━', progress_width - finished));
                                }
                            }

                        if ((progress_total_width - progress_width) is int remaining and > 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write(new string('━', remaining));
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

                    oldp = progr;

                    sw_report.Restart();
                }
                else
                    await Task.Delay(Settings.report_interval_ms / 3);
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
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
            nameof(SaveTiledPNG),
            nameof(ComputeTiles)
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
            Settings = Settings.DefaultSettings;

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("An error occured. The default settings have been loaded.");
        }
        finally
        {
            _progress = new precision[Settings.batches];
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

        Bitmap[,] bmp = ComputeTiles(tiles);
        Colorize? fx = null;

        if (Settings.grayscale && Settings.color_map is string name)
        {
            PropertyInfo? property = typeof(ColorMap).GetProperties(BindingFlags.Public | BindingFlags.Static)
                                                     .FirstOrDefault(p => p.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));

            if (property?.GetValue(null) is ColorMap map)
                fx = new(Settings.reverse_color_map ? map.Reverse() : map);
        }

        for (int x = 0; x < tiles; ++x)
            for (int y = 0; y < tiles; ++y)
            {
                string path = string.Format(Settings.export.path_png, x, y);
                Bitmap b = bmp[x, y];

                using (b = fx?.ApplyTo(b) ?? b)
                {
                    SetExifData(b, 0x0131, "Buddhaslice by Unknown6656");
                    SetExifData(b, 0x013b, "Unknown6656");
                    SetExifData(b, 0x8298, $"Copyright (c) 2019-{DateTime.UtcNow.Year}, Unknown6656");
                    SetExifData(b, 0x010e, $"""
                    WIDTH:               {Settings.width:N0} px
                    HEIGHT:              {Settings.height:N0} px
                    ITERATIONS:          {Settings.max_iter:N0}
                    BATCHES:             {Settings.batches}
                    CORES:               {Environment.ProcessorCount}
                    DPP:                 {Settings.dpp}
                    SLICE LEVELS:        {Settings.slice_offset}...{Settings.slice_offset + Settings.slice_count}
                    TILE (X, Y):         ({x}, {y})
                    TILE WIDTH:          {b.Width}
                    TILE HEIGHT:         {b.Height}
                    MASK PATH:           "{Settings.mask.path}"
                    THRESHOLD B -> G:    {Settings.threshold_r}
                    THRESHOLD G -> R:    {Settings.threshold_g}
                    SAVING TIME (LOCAL): {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
                    """);
                    b.Save(path, ImageFormat.Png);
                }

                _queue_rendered.Enqueue((path, true));
            }
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
            wr.Write(pixel->Iterations);
        }

        wr.Flush();
        fs.Flush();
        fs.Close();

        _queue_rendered.Enqueue((Settings.export.path_raw, true));
    }

    #endregion

    private static unsafe Bitmap[,] ComputeTiles(int tilecount)
    {
        ImageTiler<precision> tiler = new(_image, &PixelTranslator);
        precision mean = 0, stddev = 0;

        for (ulong i = 0; i < _image.ItemCount; ++i)
            mean += _image[i]->Iterations;

        mean /= _image.ItemCount;

        for (ulong i = 0; i < _image.ItemCount; ++i)
        {
            precision diff = _image[i]->Iterations - mean;

            stddev += diff * diff;
        }

        stddev = (precision)Math.Sqrt(stddev / _image.ItemCount);

        precision norm_factor = 1 / (precision)(mean + stddev);

        return tiler.GenerateTiles(tilecount, tilecount, (int)Settings.width, (int)Settings.height, norm_factor);
        static uint PixelTranslator(PIXEL* pixel, precision norm_factor)
        {
            precision iterations = pixel->Iterations * norm_factor;

            if (iterations > 1)
                iterations = 1;
            else if (!(iterations > 0))
                iterations = 0;

            precision b = Math.Min(iterations - 5 * norm_factor, Settings.threshold_g);
            precision g = Math.Min(iterations, Settings.threshold_r) - Settings.threshold_g;
            precision r = iterations - Settings.threshold_r - Settings.threshold_g;
            static uint to_byte(double value) => value < 0 ? 0u : value > 1 ? 255u : (uint)(value * 255);

            if (Settings.grayscale)
                r = g = b = iterations;

            return 0xff000000u
                 | (to_byte(r) << 16)
                 | (to_byte(g) << 8)
                 | to_byte(b);
        }
    }

    private static unsafe void RenderImage()
    {
        ulong batches = (ulong)Settings.batches;
        int cores = Settings.cores;

        if (cores < 0)
            cores = Environment.ProcessorCount;

        if (Environment.ProcessorCount is int cpu_cores)
            cores = Math.Min(cpu_cores > 4 ? cpu_cores - 1 : cpu_cores, cores);

        int mask_width = _mask.GetLength(0);
        int mask_height = _mask.GetLength(1);
        ulong image_width = Settings.width;
        ulong image_height = Settings.height;
        int dpp = Settings.dpp;
        int slice_offset = Settings.slice_offset;
        int slice_count = Settings.slice_count;
        int max_iter = Settings.max_iter;

        ActionBlock<ulong> block = new(batch =>
        {
            Bounds i_bounds = Settings.bounds;
            Bounds m_bounds = Settings.mask.bounds;
            render_args args = new()
            {
                batches = batches,
                batch = batch,
                mask_width = mask_width,
                mask_height = mask_height,
                i_bounds = &i_bounds,
                m_bounds = &m_bounds,
                image_width = image_width,
                image_height = image_height,
                dpp = dpp,
                slice_offset = slice_offset,
                slice_count = slice_count,
                max_iter = max_iter,
                mask = static (x, y) => _mask[x, y],
                progress = static (index, p) => _progress[index] = p,
                image = static (index, incr) => _image[index]->Iterations += incr,
                computed = static index => _image[index]->Computed = true
            };

            if (Settings.native)
                RenderImageCore_Native(ref args);
            else
                RenderImageCore_Managed(args);
        }, new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = cores });

        for (ulong batch = 0; batch < batches; ++batch)
            block.Post(batch);

        block.Complete();
        block.Completion.Wait();
    }

    private static unsafe void RenderImageCore_Managed(render_args args)
    {
        for (ulong index = args.batch, total = args.image_width * args.image_height; index < total; index += args.batches)
        {
            ulong px = index % args.image_width;
            ulong py = index / args.image_width;

            for (int x_dpp = 0; x_dpp < args.dpp; ++x_dpp)
            {
                precision re = (px * args.i_bounds->Width * args.dpp + x_dpp) / ((precision)args.image_width * args.dpp) + args.i_bounds->left;

                for (int y_dpp = 0; y_dpp < args.dpp; ++y_dpp)
                {
                    precision im = (py * args.i_bounds->Height * args.dpp + y_dpp) / ((precision)args.image_height * args.dpp) + args.i_bounds->top;
                    bool compute = true;

                    if (args.m_bounds->Contains(re, im))
                    {
                        int px_mask = (int)((re - args.m_bounds->left) / args.m_bounds->Width * args.mask_width);
                        int py_mask = (int)((im - args.m_bounds->top) / args.m_bounds->Height * args.mask_height);

                        compute = _mask[px_mask, py_mask];
                    }

                    if (compute)
                    {
                        int iteration_count = 0;
                        Complex[] slices = new Complex[args.slice_count];
                        Complex z = 0;
                        Complex c = new(re, im);

                        do
                        {
                            z = z * z + c;

                            if (iteration_count - args.slice_offset is int i and >= 0 && i < slices.Length)
                                slices[i] = z;
                        }
                        while (Math.Abs(z.Real) < 2 && Math.Abs(z.Imaginary) < 2 && iteration_count++ < args.max_iter);

                        if (iteration_count < args.max_iter)
                            for (int slice = 0; slice < slices.Length; slice++)
                            {
                                Complex q = slices[slice];
                                ulong x_index = (ulong)((q.Real - args.i_bounds->left) * args.image_width / args.i_bounds->Width);
                                ulong y_index = (ulong)((q.Imaginary - args.i_bounds->top) * args.image_height / args.i_bounds->Height);

                                if (x_index >= 0 && x_index < args.image_width && y_index >= 0 && y_index < args.image_height)
                                    _image[(y_index * args.image_width) + x_index]->Iterations += iteration_count - slice;
                                //  ++_image[(y_index * args.image_width) + x_index]->Iterations;
                            }
                    }
                }

                _progress[args.batch] = (index * (precision)args.dpp + x_dpp) / (total * (precision)args.dpp);
            }

            _image[index]->Computed = true;
        }

        _progress[args.batch] = 1;
    }
}

internal unsafe struct render_args
{
    public ulong batches;
    public ulong batch;
    public int mask_width;
    public int mask_height;
    public Bounds* i_bounds;
    public Bounds* m_bounds;
    public ulong image_width;
    public ulong image_height;
    public int dpp;
    public int slice_offset;
    public int slice_count;
    public int max_iter;
    public mask_callback mask;
    public progress_callback progress;
    public image_callback image;
    public computed_callback computed;


    public delegate bool mask_callback(int x, int y);
    public delegate void progress_callback(int index, precision progress);
    public delegate void computed_callback(ulong index);
    public delegate void image_callback(ulong index, int increment);
}
