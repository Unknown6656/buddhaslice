//#define DOUBLE_PRECISION
#define USE_INTRINSIC_ABS


#ifdef __cplusplus
#define EXPORT extern "C" __declspec(dllexport)
#define NOEXCEPT noexcept
#else
#define EXPORT __declspec(dllexport)
#define NOEXCEPT
#endif

#define CALLCONV __cdecl


#ifdef DOUBLE_PRECISION
typedef double               precision;
#else
typedef float                precision;
#endif
typedef signed char          i8;
typedef short                i16;
typedef int                  i32;
typedef long long            i64;
typedef unsigned char        u8;
typedef unsigned short       u16;
typedef unsigned int         u32;
typedef unsigned long long   u64;
#ifndef __cplusplus
typedef enum { false, true } bool;
#endif


typedef struct
{
    precision left;
    precision right;
    precision top;
    precision bottom;
} bounds;

static inline precision __fastcall bounds_width(const bounds* const b)
{
    return b->right - b->left;
}

static inline precision __fastcall bounds_height(const bounds* const b)
{
    return b->bottom - b->top;
}

static inline bool __fastcall bounds_contains(const bounds* const b, const precision x, const precision y)
{
    return b->left <= x && x <= b->right && b->top <= y && y <= b->bottom;
}


typedef struct
{
    precision real;
    precision imag;
} complex;

static inline complex __fastcall complex_mul(const complex* const c1, const complex* const c2)
{
    const complex res = {
        c1->real * c2->real - c1->imag * c2->imag,
        c1->real * c2->imag + c1->imag * c2->real,
    };

    return res;
}

static inline complex __fastcall complex_add(const complex* const c1, const complex* const c2)
{
    const complex res = {
        c1->real + c2->real,
        c1->imag + c2->imag
    };

    return res;
}


#ifndef USE_INTRINSIC_ABS
static inline precision __abs(const precision value)
{
#ifdef DOUBLE_PRECISION
    const i64 ival = *(i64* const)&value & 0x7fffffffffffffff;
#else
    const i32 ival = *(i32* const)&value & 0x7fffffff;
#endif
    return *(precision* const)&ival;
}
#define abs __abs
#endif

typedef struct
{
    const u64 batches;
    const u64 batch;
    const i32 mask_width;
    const i32 mask_height;
    const bounds* const i_bounds;
    const bounds* const m_bounds;
    const u64 image_width;
    const u64 image_height;
    const i32 dpp;
    const i32 slice_offset;
    const i32 slice_count;
    const i32 max_iter;
    complex* const slices;

    bool(*const mask)(i32 mask_x, i32 mask_y);
    void(*const progress)(i32 batch, precision progress);
    void(*const image)(u64 index, i32 add_iterations);
    void(*const computed)(u64 index);
} render_args;

EXPORT void CALLCONV render_image_core(const render_args* const args) NOEXCEPT
{
    i32 iteration_count, i, slice, px_mask, py_mask, x_dpp, y_dpp;
    u64 x_index, y_index, px, py;
    complex z, c;
    bool compute;
    precision re, im;

    for (u64 index = args->batch, total = args->image_width * args->image_height; index < total; index += args->batches)
    {
        px = index % args->image_width;
        py = index / args->image_width;

        if (args->slices)
            for (x_dpp = 0; x_dpp < args->dpp; ++x_dpp)
            {
                re = (px * bounds_width(args->i_bounds) * args->dpp + x_dpp) / ((precision)args->image_width * args->dpp) + args->i_bounds->left;

                for (y_dpp = 0; y_dpp < args->dpp; ++y_dpp)
                {
                    im = (py * bounds_height(args->i_bounds) * args->dpp + y_dpp) / ((precision)args->image_height * args->dpp) + args->i_bounds->top;
                    compute = true;

                    if (bounds_contains(args->m_bounds, re, im))
                    {
                        px_mask = (i32)((re - args->m_bounds->left) / bounds_width(args->m_bounds) * args->mask_width);
                        py_mask = (i32)((im - args->m_bounds->top) / bounds_height(args->m_bounds) * args->mask_height);
                        compute = args->mask(px_mask, py_mask);
                    }

                    if (compute)
                    {
                        iteration_count = 0;
                        z.real = 0;
                        z.imag = 0;
                        c.real = re;
                        c.imag = im;

                        for (i = 0; i < args->slice_count; ++i)
                        {
                            args->slices[i].real = 0;
                            args->slices[i].imag = 0;
                        }

                        do
                        {
                            z = complex_mul(&z, &z);
                            z = complex_add(&z, &c);
                            i = iteration_count - args->slice_offset;

                            if (i >= 0 && i < args->slice_count)
                                args->slices[i] = z;
                        } while (abs(z.real) < 2 && abs(z.imag) < 2 && iteration_count++ < args->max_iter);

                        if (iteration_count < args->max_iter)
                            for (slice = 0; slice < args->slice_count; ++slice)
                            {
                                x_index = (u64)((args->slices[slice].real - args->i_bounds->left) * args->image_width / bounds_width(args->i_bounds));
                                y_index = (u64)((args->slices[slice].imag - args->i_bounds->top) * args->image_height / bounds_height(args->i_bounds));

                                if (x_index >= 0 && x_index < args->image_width && y_index >= 0 && y_index < args->image_height)
                                    args->image((y_index * args->image_width) + x_index, iteration_count - slice /* 1 */);
                            }
                    }
                }

                args->progress(args->batch, (index * (precision)args->dpp + x_dpp) / (total * (precision)args->dpp));
            }

        args->computed(index);
    }

    args->progress(args->batch, 1);
}

