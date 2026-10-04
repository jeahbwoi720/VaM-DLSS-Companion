// VaM DLSS - Model Resolution: how far away the room is, from the headset's two cameras.
//
// The lenses stand eight centimetres apart, so a thing shows at a different place in each, by more
// the nearer it is. For a grid of lines of sight from the middle of the head, and a set of
// distances along each, the point is found in both lenses and the two are compared; the distances
// are then chosen so that each line agrees with its lenses and with the lines beside it
// (semi-global matching along the grid's rows and columns, both ways).
//
// It is done on the processor, on a thread of its own, at a coarse scale: a hand held out is large
// and blurred, and at a fine scale the two lenses agree on it nowhere in particular.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstring>

namespace vwsdepth
{
const int kCells = 96;      // the grid, per side
const int kPlanes = 48;     // the distances tried, evenly spaced in 1/distance
const float kMost = 6.0f;   // the nearest of them, in 1/metres
const float kReach = 1.2f;  // how far the grid reaches to each side, as a tangent
const int kShrink = 8;      // camera texels per texel of the pictures compared
const int kWindow = 7;      // cells the comparison is averaged over
const int kStep = 4, kJump = 77; // what a neighbour's distance being one step off, or elsewhere altogether, costs
const int kShrinkBack = 2;  // cells a near thing is brought back in by, on every side
const int kUnknown = 64;   // the comparison where there is nothing to compare

// The numbers of the camera, as the passthrough's config block has them.
const int kFocalAt = 7, kPolyAt = 8, kCentreAt = 12, kCamToHeadAt = 24, kCalibration = 48;

struct Solver
{
    float made[kCalibration] = {};
    uint32_t madeW = 0, madeH = 0;
    int fw = 0, fh = 0;
    float* table = nullptr;     // per cell, distance, lens: where in that lens's small picture (x, y); x < 0 where the lens does not see it
    float* feature = nullptr;   // per lens: how much each texel of the small picture differs from its surroundings
    uint8_t* blind = nullptr;   // per lens: the surroundings are too bright to tell anything
    float* work[3] = {};
    uint8_t* cost = nullptr;    // per cell, distance
    uint16_t* rowSum = nullptr;
    uint16_t* total = nullptr;
    uint8_t* seen = nullptr;    // per cell: enough of the distances lie in both lenses
    uint8_t* raw = nullptr;
    uint8_t* wider = nullptr;
    uint8_t* history[3] = {};
    int turn = 0, frames = 0;

    ~Solver()
    {
        Free();
    }

    void Free()
    {
        delete[] table;
        delete[] feature;
        delete[] blind;
        delete[] cost;
        delete[] rowSum;
        delete[] total;
        delete[] seen;
        delete[] raw;
        delete[] wider;

        for (float*& p : work)
        {
            delete[] p;
            p = nullptr;
        }

        for (uint8_t*& p : history)
        {
            delete[] p;
            p = nullptr;
        }

        table = feature = nullptr;
        blind = cost = seen = raw = wider = nullptr;
        rowSum = total = nullptr;
        madeW = madeH = 0;
    }

    // Where every cell's line of sight, at every distance, falls in each lens. Made once per
    // camera and calibration.
    void Prepare(uint32_t w, uint32_t h, const float* cfg)
    {
        if (table != nullptr && madeW == w && madeH == h && memcmp(made, cfg, sizeof(made)) == 0)
            return;

        Free();
        memcpy(made, cfg, sizeof(made));
        madeW = w;
        madeH = h;

        const int lensW = (int) (w / 2);
        fw = lensW / kShrink;
        fh = (int) h / kShrink;

        const size_t cells = (size_t) kCells * kCells;
        table = new float[cells * kPlanes * 4];
        feature = new float[(size_t) fw * fh * 2];
        blind = new uint8_t[(size_t) fw * fh * 2];
        cost = new uint8_t[cells * kPlanes];
        rowSum = new uint16_t[cells * kPlanes];
        total = new uint16_t[cells * kPlanes];
        seen = new uint8_t[cells];
        raw = new uint8_t[cells];
        wider = new uint8_t[cells];

        for (float*& p : work)
            p = new float[(size_t) fw * fh];

        for (uint8_t*& p : history)
        {
            p = new uint8_t[cells];
            memset(p, 0, cells);
        }

        turn = frames = 0;

        const double focal = cfg[kFocalAt];
        const double edge = lensW * 0.5 - 12.0;

        for (int y = 0; y < kCells; ++y)
        {
            for (int x = 0; x < kCells; ++x)
            {
                const double tx = (2.0 * (x + 0.5) / kCells - 1.0) * kReach, ty = (2.0 * (y + 0.5) / kCells - 1.0) * kReach;
                const double len = sqrt(tx * tx + ty * ty + 1.0);
                const double dir[3] = { tx / len, -ty / len, -1.0 / len };
                float* out = table + ((size_t) (y * kCells + x) * kPlanes) * 4;

                for (int p = 0; p < kPlanes; ++p)
                {
                    const double distance = 1.0 / ((p + 0.5) / kPlanes * kMost);

                    for (int lens = 0; lens < 2; ++lens)
                    {
                        // the head's point in the camera: the transpose of its turn, after its place
                        const float* m = cfg + kCamToHeadAt + lens * 12;
                        const double at[3] = { dir[0] * distance - m[3], dir[1] * distance - m[7], dir[2] * distance - m[11] };
                        const double q[3] = { m[0] * at[0] + m[4] * at[1] + m[8] * at[2], m[1] * at[0] + m[5] * at[1] + m[9] * at[2],
                                              m[2] * at[0] + m[6] * at[1] + m[10] * at[2] };
                        const double across = sqrt(q[0] * q[0] + q[1] * q[1]) + 1e-12;
                        const double angle = atan2(across, -q[2]);
                        const double a2 = angle * angle;
                        const double radius = focal * angle * (1.0 + a2 * (cfg[kPolyAt] + a2 * (cfg[kPolyAt + 1] + a2 * (cfg[kPolyAt + 2] + a2 * cfg[kPolyAt + 3]))));
                        const double u = cfg[kCentreAt + lens * 2] + radius * q[0] / across, v = cfg[kCentreAt + lens * 2 + 1] - radius * q[1] / across;
                        const double fx = (u + 0.5) / kShrink - 0.5, fy = (v + 0.5) / kShrink - 0.5;
                        const bool inside = radius < edge && fx >= 0.0 && fy >= 0.0 && fx <= fw - 1.001 && fy <= fh - 1.001;
                        out[p * 4 + lens * 2] = inside ? (float) fx : -1.0f;
                        out[p * 4 + lens * 2 + 1] = (float) fy;
                    }
                }
            }
        }
    }

    static void Box(const float* in, float* out, float* tmp, int w, int h, int n)
    {
        const int half = n / 2;

        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                float sum = 0.0f;

                for (int d = -half; d <= half; ++d)
                {
                    const int at = x + d < 0 ? 0 : x + d >= w ? w - 1 : x + d;
                    sum += in[y * w + at];
                }

                tmp[y * w + x] = sum;
            }
        }

        const float scale = 1.0f / (float) (n * n);

        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                float sum = 0.0f;

                for (int d = -half; d <= half; ++d)
                {
                    const int at = y + d < 0 ? 0 : y + d >= h ? h - 1 : y + d;
                    sum += tmp[at * w + x];
                }

                out[y * w + x] = sum * scale;
            }
        }
    }

    // What there is to compare: each lens's picture made little, and of that how much a spot
    // differs from its surroundings, as a share of them -- which a difference in exposure between
    // the lenses does not change.
    void Features(const uint8_t* grey)
    {
        const int lensW = (int) (madeW / 2);
        const float area = 1.0f / (float) (kShrink * kShrink);

        for (int lens = 0; lens < 2; ++lens)
        {
            float* little = work[0];

            for (int y = 0; y < fh; ++y)
            {
                for (int x = 0; x < fw; ++x)
                {
                    uint32_t sum = 0;

                    for (int j = 0; j < kShrink; ++j)
                    {
                        const uint8_t* row = grey + (size_t) (y * kShrink + j) * madeW + lens * lensW + x * kShrink;

                        for (int i = 0; i < kShrink; ++i)
                            sum += row[i];
                    }

                    little[y * fw + x] = (float) sum * area;
                }
            }

            Box(little, work[1], work[2], fw, fh, 7);
            Box(work[1], work[1], work[2], fw, fh, 7);

            float* f = feature + (size_t) lens * fw * fh;
            uint8_t* b = blind + (size_t) lens * fw * fh;

            for (int i = 0; i < fw * fh; ++i)
            {
                const float wide = work[1][i];
                const float v = 4.0f * (little[i] - wide) / (wide + 8.0f);
                f[i] = v < -1.0f ? -1.0f : v > 1.0f ? 1.0f : v;
                // A lit screen or a window: what the lenses show there is glare and flicker.
                b[i] = wide > 215.0f ? 1 : 0;
            }
        }
    }

    void Compare()
    {
        const size_t cells = (size_t) kCells * kCells;
        const size_t texels = (size_t) fw * fh;

        for (size_t c = 0; c < cells; ++c)
        {
            const float* t = table + c * kPlanes * 4;
            uint8_t* out = (uint8_t*) rowSum; // the unaveraged comparison, kept where the sums will go
            out += c * kPlanes;
            int inBoth = 0;

            for (int p = 0; p < kPlanes; ++p, t += 4)
            {
                if (t[0] < 0.0f || t[2] < 0.0f)
                {
                    out[p] = (uint8_t) kUnknown;
                    continue;
                }

                inBoth++;
                float value[2];
                bool dazzled = false;

                for (int lens = 0; lens < 2; ++lens)
                {
                    const float fx = t[lens * 2], fy = t[lens * 2 + 1];
                    const int x0 = (int) fx, y0 = (int) fy;
                    const float ax = fx - x0, ay = fy - y0;
                    const float* f = feature + lens * texels + (size_t) y0 * fw + x0;
                    value[lens] = (f[0] * (1.0f - ax) + f[1] * ax) * (1.0f - ay) + (f[fw] * (1.0f - ax) + f[fw + 1] * ax) * ay;
                    dazzled = dazzled || blind[lens * texels + (size_t) y0 * fw + x0] != 0;
                }

                const float d = fabsf(value[0] - value[1]) * 127.5f;
                out[p] = dazzled ? (uint8_t) kUnknown : (uint8_t) (d > 255.0f ? 255.0f : d);
            }

            seen[c] = inBoth * 2 > kPlanes ? 1 : 0;
        }

        // Averaged over the cells around: across, then down.
        const uint8_t* plain = (const uint8_t*) rowSum;
        uint16_t* across = total;
        const int half = kWindow / 2;

        for (int y = 0; y < kCells; ++y)
        {
            for (int x = 0; x < kCells; ++x)
            {
                uint16_t* o = across + (size_t) (y * kCells + x) * kPlanes;
                memset(o, 0, kPlanes * sizeof(uint16_t));

                for (int d = -half; d <= half; ++d)
                {
                    const int at = x + d < 0 ? 0 : x + d >= kCells ? kCells - 1 : x + d;
                    const uint8_t* in = plain + (size_t) (y * kCells + at) * kPlanes;

                    for (int p = 0; p < kPlanes; ++p)
                        o[p] = (uint16_t) (o[p] + in[p]);
                }
            }
        }

        for (int y = 0; y < kCells; ++y)
        {
            for (int x = 0; x < kCells; ++x)
            {
                uint16_t sum[kPlanes] = {};

                for (int d = -half; d <= half; ++d)
                {
                    const int at = y + d < 0 ? 0 : y + d >= kCells ? kCells - 1 : y + d;
                    const uint16_t* in = across + (size_t) (at * kCells + x) * kPlanes;

                    for (int p = 0; p < kPlanes; ++p)
                        sum[p] = (uint16_t) (sum[p] + in[p]);
                }

                uint8_t* o = cost + (size_t) (y * kCells + x) * kPlanes;

                for (int p = 0; p < kPlanes; ++p)
                    o[p] = (uint8_t) (sum[p] / (kWindow * kWindow));
            }
        }
    }

    // One line of cells, one way: each cell's comparison plus the least it costs to get there from
    // the cell before, where staying at a distance is free, one step is cheap and a jump is dear.
    void Path(int start, int step, int count)
    {
        uint16_t before[kPlanes], now[kPlanes];
        const uint8_t* c = cost + (size_t) start * kPlanes;
        uint16_t* t = total + (size_t) start * kPlanes;

        for (int p = 0; p < kPlanes; ++p)
        {
            before[p] = c[p];
            t[p] = (uint16_t) (t[p] + c[p]);
        }

        for (int i = 1; i < count; ++i)
        {
            const int cell = start + i * step;
            c = cost + (size_t) cell * kPlanes;
            t = total + (size_t) cell * kPlanes;

            uint16_t least = before[0];

            for (int p = 1; p < kPlanes; ++p)
                least = before[p] < least ? before[p] : least;

            const uint16_t jump = (uint16_t) (least + kJump);

            for (int p = 0; p < kPlanes; ++p)
            {
                uint16_t best = before[p];

                if (p > 0 && before[p - 1] + kStep < best)
                    best = (uint16_t) (before[p - 1] + kStep);

                if (p < kPlanes - 1 && before[p + 1] + kStep < best)
                    best = (uint16_t) (before[p + 1] + kStep);

                if (jump < best)
                    best = jump;

                now[p] = (uint16_t) (c[p] + best - least);
                t[p] = (uint16_t) (t[p] + now[p]);
            }

            memcpy(before, now, sizeof(before));
        }
    }

    // The nearest (or the farthest) of the cells within `reach` of each, among those in both lenses.
    void Spread(const uint8_t* in, uint8_t* to, int reach, bool nearest) const
    {
        for (int y = 0; y < kCells; ++y)
        {
            for (int x = 0; x < kCells; ++x)
            {
                uint8_t pick = in[y * kCells + x];

                for (int j = -reach; j <= reach; ++j)
                {
                    const int yy = y + j;

                    if (yy < 0 || yy >= kCells)
                        continue;

                    for (int i = -reach; i <= reach; ++i)
                    {
                        const int xx = x + i;

                        if (xx < 0 || xx >= kCells || seen[yy * kCells + xx] == 0)
                            continue;

                        const uint8_t v = in[yy * kCells + xx];
                        pick = nearest ? (v > pick ? v : pick) : (v < pick ? v : pick);
                    }
                }

                to[y * kCells + x] = seen[y * kCells + x] != 0 ? pick : 0;
            }
        }
    }

    static uint8_t Middle(uint8_t* v, int n)
    {
        for (int i = 1; i < n; ++i)
        {
            const uint8_t k = v[i];
            int j = i - 1;

            for (; j >= 0 && v[j] > k; --j)
                v[j + 1] = v[j];

            v[j + 1] = k;
        }

        return v[n / 2];
    }

    // `grey`: both lenses side by side, w x h. `out`: kCells x kCells, RGBA, first row the top:
    // r = 1/distance as a share of kMost as this frame has it, g = the line of sight lies in both
    // lenses, b = the same as r with specks taken out and steadied over three frames -- the one
    // to use.
    void Solve(const uint8_t* grey, uint32_t w, uint32_t h, const float* cfg, uint8_t* out)
    {
        Prepare(w, h, cfg);
        Features(grey);
        Compare();

        const size_t cells = (size_t) kCells * kCells;
        memset(total, 0, cells * kPlanes * sizeof(uint16_t));

        for (int i = 0; i < kCells; ++i)
        {
            Path(i * kCells, 1, kCells);
            Path(i * kCells + kCells - 1, -1, kCells);
            Path(i, kCells, kCells);
            Path((kCells - 1) * kCells + i, -kCells, kCells);
        }

        for (size_t c = 0; c < cells; ++c)
        {
            const uint16_t* t = total + c * kPlanes;
            int at = 0;

            for (int p = 1; p < kPlanes; ++p)
                at = t[p] < t[at] ? p : at;

            // between two distances, where a curve through the least and its neighbours bottoms out
            float fraction = 0.0f;

            if (at > 0 && at < kPlanes - 1)
            {
                const float bend = (float) t[at - 1] - 2.0f * (float) t[at] + (float) t[at + 1];

                if (bend > 0.5f)
                    fraction = 0.5f * ((float) t[at - 1] - (float) t[at + 1]) / bend;
            }

            const float share = ((float) at + 0.5f + fraction) / (float) kPlanes;
            raw[c] = seen[c] != 0 ? (uint8_t) (share * 255.0f + 0.5f) : 0;
        }

        // Specks out: the middle one of each cell and its eight neighbours.
        uint8_t* fresh = history[turn];

        for (int y = 0; y < kCells; ++y)
        {
            for (int x = 0; x < kCells; ++x)
            {
                uint8_t v[9];
                int n = 0;

                for (int j = -1; j <= 1; ++j)
                {
                    for (int i = -1; i <= 1; ++i)
                    {
                        const int yy = y + j < 0 ? 0 : y + j >= kCells ? kCells - 1 : y + j;
                        const int xx = x + i < 0 ? 0 : x + i >= kCells ? kCells - 1 : x + i;
                        v[n++] = raw[yy * kCells + xx];
                    }
                }

                fresh[y * kCells + x] = Middle(v, 9);
            }
        }

        turn = (turn + 1) % 3;
        frames++;

        for (size_t c = 0; c < cells; ++c)
        {
            uint8_t v[3] = { history[0][c], history[1][c], history[2][c] };
            out[c * 4] = raw[c];
            out[c * 4 + 1] = seen[c] != 0 ? 255 : 0;
            out[c * 4 + 3] = 255;
            raw[c] = frames >= 3 ? Middle(v, 3) : fresh[c];
        }

        // A near thing comes out with holes in it, where the lenses found nothing to agree on,
        // and wider than it is by about half the window on every side: what lies just beside it
        // is compared through a window that still holds its edge. So the nearest of the cells
        // around is taken first, which closes the holes, and then the farthest of twice as many,
        // which opens them no more and brings the edge back in.
        Spread(raw, wider, kShrinkBack, true);
        Spread(wider, raw, kShrinkBack * 2, false);

        for (size_t c = 0; c < cells; ++c)
            out[c * 4 + 2] = raw[c];
    }
};
}
