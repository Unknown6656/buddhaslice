#nullable enable

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Drawing.Imaging;
using System.Drawing;

using Unknown6656.BigFuckingAllocator;

namespace buddhaslice
{
    public sealed unsafe class ImageTiler<U>
    {
        private readonly BigFuckingAllocator<PIXEL> _buffer;
        private readonly delegate*<PIXEL*, U, uint> _pixel_translator;


        /// <param name="pixel_translator">
        /// Translation function : T --> uint32  where uint32 represents the ARGB-pixel value associated with the given instance of T.
        /// </param>
        public ImageTiler(BigFuckingAllocator<PIXEL> buffer, delegate*<PIXEL*, U, uint> pixel_translator)
        {
            _buffer = buffer;
            _pixel_translator = pixel_translator;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe Bitmap GenerateTile(int xoffs, int yoffs, int width, int height, int total_width, U argument)
        {
            Bitmap bmp = new(width, height, PixelFormat.Format32bppArgb);
            BitmapData dat = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, bmp.PixelFormat);
            uint* ptr = (uint*)dat.Scan0;

            Parallel.For(0, width * height, i =>
            {
                int x = xoffs + i % width;
                int y = yoffs + i / width;
                ulong idx = (ulong)y * (ulong)total_width + (ulong)x;

                ptr[i] = _buffer[idx]->Computed ? _pixel_translator(_buffer[idx], argument) : 0x00000000u;
            });

            bmp.UnlockBits(dat);

            return bmp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Bitmap[,] GenerateTiles(int tile_count_x, int tile_count_y, int total_width, int total_height, U argument)
        {
            Bitmap[,] bitmaps = new Bitmap[tile_count_x, tile_count_y];
            int w = total_width / tile_count_x;
            int h = total_height / tile_count_y;

            for (int x = 0; x < tile_count_x; ++x)
                for (int y = 0; y < tile_count_y; ++y)
                    bitmaps[x, y] = GenerateTile(x * w, y * h, w, h, total_width, argument);

            return bitmaps;
        }
    }
}
