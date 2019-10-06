#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Drawing.Imaging;
using System.Drawing;
using System;


namespace buddhaslice
{
    public unsafe readonly struct BigFuckingAllocator<T>
        where T : unmanaged
    {
        public const int MAX_SLICE_SIZE = 256 * 1024 * 1024;

        private readonly int _slicecount;
        private readonly int _slicesize;
        private readonly T*[] _slices;


        public readonly ulong ItemCount { get; }

        public ulong BinarySize => ItemCount * (ulong)sizeof(T);

        public int SliceCount => _slices.Length;

        public readonly T* this[ulong idx]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => idx < ItemCount ? _slices[idx / (ulong)_slicesize] + idx % (ulong)_slicesize
                                    : throw new ArgumentOutOfRangeException(nameof(idx), idx, $"The index must be smaller than {ItemCount}.");
        }

        public readonly Span<T> this[Range range]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                (int offs, int len) = range.GetOffsetAndLength((int)ItemCount);

                Span<T> span = new T[len];

                for (int i = 0; i < offs; ++i)
                    span[i] = *this[(ulong)offs + (ulong)i];

                return span;
            }
        }


        static BigFuckingAllocator()
        {
            if (sizeof(T) > MAX_SLICE_SIZE)
                throw new ArgumentException($"The generic parameter type '{typeof(T)}' cannot be used, as it exceeds the {MAX_SLICE_SIZE} byte limit.", "T");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BigFuckingAllocator(T[] array)
            : this((ulong)array.LongLength)
        {
            for (long i = 0; i < array.LongLength; ++i)
                *this[(ulong)i] = array[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BigFuckingAllocator(Span<T> memory)
            : this(memory.ToArray())
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BigFuckingAllocator(T* pointer, int count)
        {
            ItemCount = (ulong)count;
            _slicesize = count;
            _slicecount = 0;
            _slices = new[] { pointer };
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
                try
                {
                    Marshal.FreeHGlobal((IntPtr)_slices[i]);
                }
                catch
                {
                    _slices[i] = null;
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void AggressivelyClaimAllTheFuckingMemory(T value = default)
        {
            for (int i = 0; i < _slicecount; ++i)
            {
                T* ptr = _slices[i];
                byte* bptr = (byte*)ptr;
                int len = i < _slices.Length - 1 ? _slicesize : (int)(ItemCount - (ulong)i * (ulong)_slicesize);

                Parallel.For(0, len * sizeof(T), j => bptr[j] = 0xff);
                Parallel.For(0, len, j => ptr[j] = value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly Span<T> GetSlice(int index) => new Span<T>(_slices[index], index < _slices.Length - 1 ? _slicesize : (int)(ItemCount - (ulong)index * (ulong)_slicesize));

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

        public static implicit operator BigFuckingAllocator<T>(T[] array) => new BigFuckingAllocator<T>(array);

        public static implicit operator BigFuckingAllocator<T>(Span<T> span) => new BigFuckingAllocator<T>(span);

        public static implicit operator BigFuckingAllocator<T>(Memory<T> memory) => new BigFuckingAllocator<T>(memory.Span);
    }

    public sealed class ImageTiler<T>
        where T : unmanaged
    {
        private readonly BigFuckingAllocator<T> _buffer;
        private readonly Func<T, uint> _pixel_translator;


        /// <param name="pixel_translator">
        /// Translation function : T --> uint32  where uint32 represents the ARGB-pixel value associated with the given instance of T.
        /// </param>
        public ImageTiler(BigFuckingAllocator<T> buffer, Func<T, uint> pixel_translator)
        {
            _buffer = buffer;
            _pixel_translator = pixel_translator;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe Bitmap GenerateTile(int xoffs, int yoffs, int width, int height, int total_width)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData dat = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, bmp.PixelFormat);
            uint* ptr = (uint*)dat.Scan0;

            Parallel.For(0, bmp.Width * bmp.Height, i =>
            {
                int x = xoffs + i % width;
                int y = yoffs + i / width;
                ulong idx = (ulong)y * (ulong)total_width + (ulong)x;

                ptr[i] = _pixel_translator(*_buffer[idx]);
            });

            bmp.UnlockBits(dat);

            return bmp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Bitmap[,] GenerateTiles((int x, int y) tile_count, (int width, int heigt) total_pixels)
        {
            Bitmap[,] bitmaps = new Bitmap[tile_count.x, tile_count.y];
            int w = total_pixels.width / tile_count.x;
            int h = total_pixels.heigt / tile_count.y;

            for (int x = 0; x < tile_count.x; ++x)
                for (int y = 0; y < tile_count.y; ++y)
                    bitmaps[x, y] = GenerateTile(x * w, y * h, w, h, total_pixels.width);

            return bitmaps;
        }
    }
}
