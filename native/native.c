//#define DOUBLE_PRECISION

#include<stdint.h>
#include <stdlib.h>


#ifdef __cplusplus
#define EXPORT extern "C" __declspec(dllexport)
#define NOEXCEPT noexcept
#else
#define EXPORT __declspec(dllexport)
#define NOEXCEPT
#endif

#define CALLCONV __cdecl


#ifdef DOUBLE_PRECISION
typedef double precision;
#else
typedef float precision;
#endif

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

precision bounds_width(const bounds* const b)
{
    return b->right - b->left;
}

precision bounds_height(const bounds* const b)
{
    return b->bottom - b->top;
}

int bounds_contains(const bounds* const b, const precision x, const precision y)
{
    return b->left <= x && x <= b->right && b->top <= y && y <= b->bottom;
}


typedef struct
{
    precision real;
    precision imag;
} complex;

complex complex_mul(const complex* const c1, const complex* const c2)
{
    const complex res = {
        c1->real * c2->real - c1->imag * c2->imag,
        c1->real * c2->imag + c1->imag * c2->real,
    };

    return res;
}

complex complex_add(const complex* const c1, const complex* const c2)
{
    const complex res = {
        c1->real + c2->real,
        c1->imag + c2->imag
    };

    return res;
}


typedef bool(*mask_callback)(int32_t mask_x, int32_t mask_y);
typedef void(*progress_callback)(int32_t batch, precision progress);
typedef void(*image_callback)(uint64_t index, int32_t add_iterations);
typedef void(*computed_callback)(uint64_t index);

EXPORT void CALLCONV render_image_core(
    const uint64_t batches,
    const uint64_t batch,
    const int32_t mask_width,
    const int32_t mask_height,
    const bounds* const i_bounds,
    const bounds* const m_bounds,
    const uint64_t image_width,
    const uint64_t image_height,
    const int32_t dpp,
    const int32_t slice_offset,
    const int32_t slice_count,
    const int32_t max_iter,
    const mask_callback _mask,
    const progress_callback _progress,
    const image_callback _image,
    const computed_callback _computed
) NOEXCEPT
{
    for (uint64_t index = batch, total = image_width * image_height; index < total; index += batches)
    {
        uint64_t px = index % image_width;
        uint64_t py = index / image_width;

        for (int32_t x_dpp = 0; x_dpp < dpp; ++x_dpp)
        {
            precision re = (px * bounds_width(i_bounds) * dpp + x_dpp) / ((precision)image_width * dpp) + i_bounds->left;

            for (int32_t y_dpp = 0; y_dpp < dpp; ++y_dpp)
            {
                precision im = (py * bounds_height(i_bounds) * dpp + y_dpp) / ((precision)image_height * dpp) + i_bounds->top;
                bool compute = true;

                if (bounds_contains(m_bounds, re, im))
                {
                    int32_t px_mask = (int32_t)((re - m_bounds->left) / bounds_width(m_bounds) * mask_width);
                    int32_t py_mask = (int32_t)((im - m_bounds->top) / bounds_height(m_bounds) * mask_height);

                    compute = _mask(px_mask, py_mask);
                }

                if (compute)
                {
                    int32_t iteration_count = 0;
                    complex* slices = (complex*)malloc(slice_count * sizeof(complex));
                    complex z = { 0, 0 };
                    complex c = { re, im };

                    for (int i = 0; i < slice_count; ++i)
                    {
                        slices[i].real = 0;
                        slices[i].imag = 0;
                    }

                    do
                    {
                        z = complex_mul(&z, &z);
                        z = complex_add(&z, &c);

                        int i = iteration_count - slice_offset;

                        if (i >= 0 && i < slice_count)
                            slices[i] = z;
                    } while (abs(z.real) < 2 && abs(z.imag) < 2 && iteration_count++ < max_iter);

                    if (iteration_count < max_iter)
                        for (int slice = 0; slice < slice_count; ++slice)
                        {
                            complex* q = slices + slice;
                            uint64_t x_index = (uint64_t)((q->real - i_bounds->left) * image_width / bounds_width(i_bounds));
                            uint64_t y_index = (uint64_t)((q->imag - i_bounds->top) * image_height / bounds_height(i_bounds));

                            if (x_index >= 0 && x_index < image_width && y_index >= 0 && y_index < image_height)
                                _image((y_index * image_width) + x_index, iteration_count - slice /* 1 */);
                        }

                    free(slices);
                }
            }

            _progress(batch, (index * (precision)dpp + x_dpp) / (total * (precision)dpp));
        }

        _computed(index);
    }

    _progress(batch, 1);
}

