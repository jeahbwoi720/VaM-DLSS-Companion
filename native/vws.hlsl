// VamDlssNr WorkScale -- the two passes that let Neural Rendering run on a smaller raster.
//
//   PSDown     frame -> model input.  An exact area average of the frame over each model pixel's
//              footprint, taken in linear light (or a bilinear enlarge when the model runs ABOVE
//              the frame's size).
//   PSResolve  frame + model input + model output -> frame.  Only the model's EDIT is carried back
//              from the small raster ("matched residual", after Dagherbou's OptiScaler_DLSSNR fork /
//              hhkbble): result = frame + (model - modelInput), in the sRGB-encoded space the model
//              itself reads and writes, scaled so it cannot leave the unit cube. The frame
//              underneath is never resampled, so its own detail is untouched whatever the model's
//              size.
//
//   PSGuide    a guide (motion, depth, mask) -> the model's copy of it, when the model works on a
//              window: the same part of each eye as the frame's, texel for texel.
//
// The 8-bit surfaces hold sRGB-encoded values and are read and written RAW (UNORM views), with the
// conversion done here. That is deliberate for the resolve: the edit is a difference of encoded
// values, and filtering the encoded values keeps the enlargement linear in the edit -- interp(m) -
// interp(p) is interp(m - p) -- where filtering decoded values and re-encoding is not. The *_ENCODED
// flags say which surfaces arrive that way; a surface Unity typed as *_SRGB is converted by the
// hardware instead and arrives linear.
//
// THE WINDOW. The model need not be shown the whole frame. With F_WINDOW set it works on one
// rectangle of each eye -- gWindow, as fractions of the eye -- and nothing outside that rectangle is
// touched: PSDown reads only it, PSResolve lays the edit back only into it, faded to nothing along
// its edge so there is no border to see. Without the flag the window is the whole eye and every
// pass is what it was before there were windows.

cbuffer Params : register(b0)
{
    float4 gFrameSize;  // xy = size in pixels of the texture at t0 (the frame; in PSGuide the guide), zw = 1/size
    float4 gDstSize;    // xy = this pass's render target size, zw = 1/size
    float4 gWorkSize;   // xy = model raster size, zw = 1/size
    uint   gEyes;       // 1, or 2 for a double-wide stereo frame
    uint   gFlags;
    float  gStrength;   // resolve: how much of the edit lands (1 = all of it)
    uint   gMode;       // resolve: 0 matched residual, 1 classic, 2 show the edit, 3 frame untouched
    float4 gWindow[2];  // per eye: xy = origin, zw = size of the model's window, as fractions of that eye
    float4 gFade;       // x = how far in from the window's edge the edit fades, as a fraction of its half-size
    float4 gPrev[2];    // guide: per eye, the window as it was a frame ago (origin xy, size zw), for F_MOVED
};

static const uint F_FRAME_ENCODED = 1u; // the frame's values are sRGB-encoded as sampled
static const uint F_PROXY_ENCODED = 2u; // likewise the model input's
static const uint F_MODEL_ENCODED = 4u; // likewise the model output's
static const uint F_OUT_ENCODED   = 8u; // the render target stores what is written, unconverted
static const uint F_WINDOW        = 16u; // the model works on gWindow, not on the whole of each eye
static const uint F_MOVED         = 32u; // guide: these are motion vectors and the window is not where, or what size, it was (gPrev)
static const uint F_TOP_DOWN      = 64u; // guide: the textures' first row is the TOP of the picture (motion's y points up it)

Texture2D<float4> tFrame : register(t0);
Texture2D<float4> tProxy : register(t1);
Texture2D<float4> tModel : register(t2);
SamplerState      sLinear : register(s0);

// Both saturate first. That bounds the value to what an 8-bit model input can carry, and under
// D3D's rules saturate maps NaN to 0, so one bad pixel cannot poison a whole footprint's average.
float3 SrgbToLinear(float3 v)
{
    v = saturate(v);
    float3 lo = v / 12.92;
    float3 hi = pow((v + 0.055) / 1.055, 2.4);
    return lerp(hi, lo, step(v, 0.04045));
}

float3 LinearToSrgb(float3 v)
{
    v = saturate(v);
    float3 lo = v * 12.92;
    float3 hi = 1.055 * pow(max(v, 1e-12), 1.0 / 2.4) - 0.055;
    return lerp(hi, lo, step(v, 0.0031308));
}

float4 Texel(float4 c, bool decode)
{
    c.rgb = decode ? SrgbToLinear(c.rgb) : saturate(c.rgb);
    c.a = saturate(c.a);
    return c;
}

// In a double-wide stereo frame the two eyes sit side by side, each in its own half of every
// texture. Which eye a pixel at `x` of a raster `width` across belongs to, and that eye's size.
uint EyeOf(float x, float width)
{
    return (gEyes == 2u && x >= width * 0.5) ? 1u : 0u;
}

float2 EyeSize(float2 texSize)
{
    return float2(texSize.x / (float) gEyes, texSize.y);
}

// A rectangle of texels in a texture: where it starts and how far it runs.
struct Region
{
    float2 origin;
    float2 size;
};

// All of eye `e` in a texture `texSize` across.
Region WholeEye(float2 texSize, uint e)
{
    Region r;
    r.size = EyeSize(texSize);
    r.origin = float2((float) e * r.size.x, 0.0);
    return r;
}

// The model's window in eye `e` of a texture `texSize` across: whole texels, so that at 1:1 the
// model is handed the frame's own pixels.
Region WindowIn(float2 texSize, uint e)
{
    Region r = WholeEye(texSize, e);

    if ((gFlags & F_WINDOW) != 0u)
    {
        const float2 eye = r.size;
        r.origin += round(gWindow[e].xy * eye);
        r.size = max(round(gWindow[e].zw * eye), 1.0);
    }

    return r;
}

// The exact box resample: integrate the texture over a footprint that starts at texel position
// `p0` and runs `ratio` texels (> 1 on at least one axis). Eye halves never mix: the footprints
// tile the region they are taken from exactly, and a region lies within one eye.
float4 AreaAverage(Texture2D<float4> t, float2 texSize, float2 p0, float2 ratio, bool decode)
{
    const float x0 = p0.x;
    const float x1 = x0 + ratio.x;
    const float y0 = p0.y;
    const float y1 = y0 + ratio.y;
    const int i0 = (int) floor(x0);
    const int j0 = (int) floor(y0);
    const int maxX = (int) texSize.x - 1;
    const int maxY = (int) texSize.y - 1;

    float4 acc = 0.0;
    float wsum = 0.0;

    [loop] for (int j = 0; j < 9; ++j)
    {
        const float fy = (float) (j0 + j);
        const float wy = min(y1, fy + 1.0) - max(y0, fy);

        if (wy <= 0.0)
            break;

        const int jj = min(j0 + j, maxY);

        [loop] for (int i = 0; i < 9; ++i)
        {
            const float fx = (float) (i0 + i);
            const float wx = min(x1, fx + 1.0) - max(x0, fx);

            if (wx <= 0.0)
                break;

            const int ii = min(i0 + i, maxX);
            const float w = wx * wy;
            acc += Texel(t.Load(int3(ii, jj, 0)), decode) * w;
            wsum += w;
        }
    }

    return acc / max(wsum, 1e-6);
}

// One read of region `src` of a texture, for pixel `q` of a destination region `dstSize` across
// (both in eye `e`), whatever their relative sizes: a load at 1:1, an area average when the source
// is larger, a bilinear tap confined to the eye when it is smaller. `decode` converts each texel
// from sRGB to linear BEFORE it is filtered; without it the stored values are filtered as they are.
float4 Fetch(Texture2D<float4> t, float4 texSize, Region src, float2 q, float2 dstSize, uint e, bool decode)
{
    const float2 ratio = src.size / dstSize;
    float4 result = 0.0;

    if (src.size.x == dstSize.x && src.size.y == dstSize.y)
    {
        result = Texel(t.Load(int3((int2) (src.origin + q), 0)), decode);
    }
    else if (ratio.x > 1.0 || ratio.y > 1.0)
    {
        result = AreaAverage(t, texSize.xy, src.origin + q * ratio, ratio, decode);
    }
    else
    {
        // Enlarging: the hardware filters whatever is stored, so a decode here comes after the
        // filter. Only an 8-bit frame being supersampled takes that route, and the difference is
        // immaterial. A tap near the middle of a double-wide texture would read the other eye's
        // edge texel; each is kept inside the half its destination pixel is in.
        const Region eye = WholeEye(texSize.xy, e);
        float2 uv = (src.origin + (q + 0.5) * ratio) * texSize.zw;
        uv.x = clamp(uv.x, (eye.origin.x + 0.5) * texSize.z, (eye.origin.x + eye.size.x - 0.5) * texSize.z);
        result = Texel(t.SampleLevel(sLinear, uv, 0), decode);
    }

    return result;
}

float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

float4 PSDown(float4 pos : SV_Position) : SV_Target
{
    // The self-test's probe: a flat grey in place of the frame, so the frame on which the model's
    // answer to it reaches the screen can be read off the screen. Never requested in normal use.
    if (gMode == 9u)
        return float4((gFlags & F_OUT_ENCODED) != 0u ? LinearToSrgb(gStrength.xxx) : saturate(gStrength.xxx), 1.0);

    // The model's raster is its window of each eye and nothing else. Averaged in linear light, as
    // any minification should be.
    const uint e = EyeOf(pos.x, gDstSize.x);
    const Region dst = WholeEye(gDstSize.xy, e);
    const float4 c = Fetch(tFrame, gFrameSize, WindowIn(gFrameSize.xy, e), floor(pos.xy) - dst.origin, dst.size, e,
                           (gFlags & F_FRAME_ENCODED) != 0u);
    return float4((gFlags & F_OUT_ENCODED) != 0u ? LinearToSrgb(c.rgb) : c.rgb, c.a);
}

// A guide for the model -- motion vectors, depth, the control mask -- cut to the same window as the
// frame. Nearest texel, as the point-filtered blit it stands in for takes them, and by fractions
// rather than whole texels because a guide need not be the frame's size. Values pass through as
// they are; a window changes what a motion vector is worth in model pixels, and that is put right
// where the vectors' scale is given to the network, not here.
//
// One thing is done to the values, and only to the motion vectors of a window that is not where,
// or not the size, it was a frame ago (one that follows the eye, or a figure on a monitor). The
// network sees the window as its whole picture: when the window moves, everything in it appears
// to move the other way, and when it grows, to shrink towards the middle -- though nothing in the
// scene did either. A motion vector says where a point was a frame ago; what the network needs is
// where it was IN THE WINDOW AS THE WINDOW THEN WAS. With p the point's place in the eye, m its
// motion there (now minus then), o and s the window's origin and size, and ' for a frame ago:
//
//     in the window now:   (p - o) / s          a frame ago:   (p - m - o') / s'
//
// VamDlssNr scales the vectors by 1/s (see SetParamsWindowed), so what is stored is the difference
// times s:  (p - o) - (s / s') (p - m - o').  With the window unchanged that is m itself.
float4 PSGuide(float4 pos : SV_Position) : SV_Target
{
    const uint e = EyeOf(pos.x, gDstSize.x);
    const Region dst = WholeEye(gDstSize.xy, e);
    const Region src = WholeEye(gFrameSize.xy, e);
    const float2 q = floor(pos.xy) - dst.origin;
    float2 at = (q + 0.5) / dst.size;

    if ((gFlags & F_WINDOW) != 0u)
        at = gWindow[e].xy + at * gWindow[e].zw;

    // The small push decides a texel boundary the same way every time.
    const float2 texel = clamp(floor(at * src.size + 1.0 / 1024.0), 0.0, src.size - 1.0);
    float4 value = tFrame.Load(int3((int2) (src.origin + texel), 0));

    if ((gFlags & F_MOVED) != 0u)
    {
        // Motion is stored with y up the picture; the window is in rows of the texture, which
        // run down the picture when F_TOP_DOWN says so. Worked in rows, stored back as it came.
        const float2 rows = float2(1.0, (gFlags & F_TOP_DOWN) != 0u ? -1.0 : 1.0);
        const float2 m = value.xy * rows;
        const float2 k = gWindow[e].zw / max(gPrev[e].zw, 1e-6);
        value.xy = ((at - gWindow[e].xy) - k * (at - m - gPrev[e].xy)) * rows;
    }

    return value;
}

// Lays an edit onto a colour without leaving the unit cube, and without changing the edit's
// direction.
//
// A per-channel clamp would bend the hue: the first channel to reach 0 or 1 stops while the others
// carry on, and the colour the model asked for becomes a different one. Instead the whole edit is
// shortened by one factor -- the largest that keeps every channel inside -- so what lands is the
// model's own change, just less of it. Each channel has `room` left in the direction it is being
// moved; the edit can be taken as far as the tightest channel allows.
//
// The idea is hhkbble's (the "matched residual" work in Dagherbou's OptiScaler_DLSSNR fork).
float3 AddEditInGamut(float3 colour, float3 edit)
{
    const float3 room = lerp(colour, 1.0 - colour, step(0.0, edit)); // towards 1 if rising, 0 if falling
    const float3 size = abs(edit);
    const float3 reach = lerp(1e6, room / max(size, 1e-6), step(1e-6, size)); // a still channel limits nothing
    return colour + edit * saturate(min(reach.x, min(reach.y, reach.z)));
}

float4 EmitEncoded(float3 encoded, float a)
{
    return float4((gFlags & F_OUT_ENCODED) != 0u ? saturate(encoded) : SrgbToLinear(encoded), a);
}

// How much of the model's work lands at a point of its window (`at`, 0..1 across it): all of it
// over the middle, none along the edge, and a smooth fall between. The shape is a rounded square --
// |x|^4 + |y|^4 -- which keeps most of the window's area and has no corner to catch the eye.
float WindowWeight(float2 at)
{
    const float2 n = abs(at * 2.0 - 1.0);
    const float2 n2 = n * n;
    const float r = sqrt(sqrt(n2.x * n2.x + n2.y * n2.y));
    return 1.0 - smoothstep(1.0 - max(gFade.x, 1e-4), 1.0, r);
}

// b where the weight is whole, exactly; the blend only where there is one.
float3 Mix(float3 a, float3 b, float w)
{
    return w >= 1.0 ? b : lerp(a, b, w);
}

float4 PSResolve(float4 pos : SV_Position) : SV_Target
{
    const uint2 d = (uint2) pos.xy;
    const float4 frame = tFrame.Load(int3(d, 0));
    const float3 frameEnc = (gFlags & F_FRAME_ENCODED) != 0u ? saturate(frame.rgb) : LinearToSrgb(frame.rgb);
    const float frameA = saturate(frame.a);

    if (gMode == 3u)
        return EmitEncoded(frameEnc, frameA);

    // Where this pixel is in the model's window of its eye. Outside it the model saw nothing and
    // the frame goes through as it is.
    const uint e = EyeOf(pos.x, gDstSize.x);
    const Region window = WindowIn(gDstSize.xy, e);
    const float2 q = floor(pos.xy) - window.origin;
    float weight = 1.0;

    if ((gFlags & F_WINDOW) != 0u)
    {
        if (q.x < 0.0 || q.y < 0.0 || q.x >= window.size.x || q.y >= window.size.y)
            return EmitEncoded(gMode == 2u ? float3(0.5, 0.5, 0.5) : frameEnc, frameA);

        weight = WindowWeight((q + 0.5) / window.size);
    }

    const Region work = WholeEye(gWorkSize.xy, e);
    const float4 proxy = Fetch(tProxy, gWorkSize, work, q, window.size, e, false);
    const float4 model = Fetch(tModel, gWorkSize, work, q, window.size, e, false);
    const float3 proxyEnc = (gFlags & F_PROXY_ENCODED) != 0u ? proxy.rgb : LinearToSrgb(proxy.rgb);
    const float3 modelEnc = (gFlags & F_MODEL_ENCODED) != 0u ? model.rgb : LinearToSrgb(model.rgb);
    const float alpha = weight >= 1.0 ? model.a : lerp(frameA, model.a, weight);

    // Classic: the model's own picture, enlarged. What a plain upscale of the output would give.
    if (gMode == 1u)
        return EmitEncoded(Mix(frameEnc, modelEnc, weight), alpha);

    // The edit. Exactly zero wherever the model left its input alone, so those pixels come back as
    // the frame itself.
    const float3 edit = (modelEnc - proxyEnc) * (gStrength * weight);

    if (gMode == 2u)
        return EmitEncoded(saturate(0.5 + edit * 4.0), alpha);

    return EmitEncoded(AddEditInGamut(frameEnc, edit), alpha);
}
