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
    float4 gFade;       // x = how far in from the window's edge the edit fades, as a fraction of its half-size;
                        // F_FOLLOW: y = how unlike the frame a model texel may be and still be listened to, z = how
                        // much the enlarged edit is sharpened, w = how far that may carry it past its neighbours
    float4 gPrev[2];    // guide: per eye, the window as it was a frame ago (origin xy, size zw), for F_MOVED
};

static const uint F_FRAME_ENCODED = 1u; // the frame's values are sRGB-encoded as sampled
static const uint F_PROXY_ENCODED = 2u; // likewise the model input's
static const uint F_MODEL_ENCODED = 4u; // likewise the model output's
static const uint F_OUT_ENCODED   = 8u; // the render target stores what is written, unconverted
static const uint F_WINDOW        = 16u; // the model works on gWindow, not on the whole of each eye
static const uint F_MOVED         = 32u; // guide: these are motion vectors and the window is not where, or what size, it was (gPrev)
static const uint F_TOP_DOWN      = 64u; // guide: the textures' first row is the TOP of the picture (motion's y points up it)
static const uint F_SQUASH        = 128u; // sharpen: the frame is scene light with no ceiling; bring it under 1 for the filter
static const uint F_FOLLOW        = 256u; // resolve: a smaller model's edit is enlarged along the frame's own edges (gFade.yz)
static const uint F_STEADY        = 512u; // resolve: the edit is read from tKept, where it is kept over frames (PSSteady)
static const uint F_FULL          = 1024u; // resolve: ...kept at the frame's own size, pixel for pixel (PSGather)
static const uint F_OUTLINE       = 2048u; // resolve: the model's window is drawn in, for seeing where it is

Texture2D<float4> tFrame : register(t0);
Texture2D<float4> tProxy : register(t1);
Texture2D<float4> tModel : register(t2);
Texture2D<float4> tKept  : register(t3);   // the edit kept over frames (rgb), and how bright the model's input was there (a):
                                            // PSSteady reads last frame's and writes this frame's; the resolve reads this frame's
Texture2D<float4> tMotion : register(t4);  // PSGather: the model-size motion vectors
Texture2D<float4> tColour : register(t5);  // the room's colours as a network guessed them (see Look)
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
    const Region src = WindowIn(gFrameSize.xy, e);
    const float2 q = floor(pos.xy) - dst.origin;
    const bool decode = (gFlags & F_FRAME_ENCODED) != 0u;
    float4 c = Fetch(tFrame, gFrameSize, src, q, dst.size, e, decode);

    // The model's raster shifted by a part of one of its pixels (gFade.zw, in the frame's pixels):
    // a different shift every frame, so that over a few frames the model has looked at every one
    // of the frame's pixels from the middle of one of its own (see PSGather).
    const float2 shrink = src.size / dst.size;
    const float2 shift = shrink.x > 1.0 && shrink.y > 1.0 ? gFade.zw : float2(0.0, 0.0);

    if (shift.x != 0.0 || shift.y != 0.0)
        c = AreaAverage(tFrame, gFrameSize.xy, clamp(src.origin + q * shrink + shift, src.origin, src.origin + max(src.size - shrink, 0.0)), shrink, decode);

    // A sharper shrink (gFade.y). The exact average is the honest one and the softest: every
    // model pixel is the mean of the frame's pixels under it, and the fine contrast between them
    // is gone before the model has seen it. Some of it is given back: the pixel is moved away
    // from the mean of an area twice as wide around it, in linear light, and never below nothing.
    // What that does to the picture is the model's to answer for -- its edit is still measured
    // against this same input, so none of the sharpening itself reaches the frame.
    const float2 ratio = src.size / dst.size;

    if (gFade.y > 0.0 && ratio.x > 1.0 && ratio.y > 1.0 && ratio.x <= 4.5 && ratio.y <= 4.5)
    {
        const float2 from = clamp(src.origin + q * ratio + shift - 0.5 * ratio, src.origin, src.origin + max(src.size - 2.0 * ratio, 0.0));
        const float3 around = AreaAverage(tFrame, gFrameSize.xy, from, 2.0 * ratio, decode).rgb;
        c.rgb = max(c.rgb + (c.rgb - around) * gFade.y, 0.0);
    }

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

// A smaller model's edit, enlarged along the frame's own edges.
//
// Enlarged plainly, the edit is a blur of what the model did at its own size: across an edge in
// the picture it runs out on both sides -- the brightening the model gave a face spills onto the
// wall behind it, in steps as wide as a model texel -- and inside a surface it is softer than the
// model made it. That is what "rougher" at a lower model resolution is.
//
// But the frame is here at full size, and each model texel's own input (the proxy) says what that
// texel was looking at. So of the four model texels around this pixel, the ones whose input looks
// like this pixel are listened to and the ones that were looking at something else are not: the
// edit meant for the face stays on the face, to the pixel. Where all four saw the same thing this
// is the plain enlargement, exactly.
//
// Then it is sharpened. What the model drew small -- pores, the grain of skin -- comes out of
// any enlargement softer and coarser than the model would have drawn it at full size, and that,
// more than the edges, is what a lower model resolution looks like. So the edit's own fine part
// (what it differs by from itself blurred over a model texel and a half each way) is given back
// stronger: gFade.z, which the caller makes follow the scale. What that may do past the values
// the four texels hold is limited (gFade.w, as a share of their range), which is what keeps a
// strong setting from ringing along edges.
float3 EditSoft(Region work, float2 at, uint e)
{
    // four bilinear taps a texel and a half out: sixteen texels' worth of blur
    const Region eye = WholeEye(gWorkSize.xy, e);
    float3 sum = 0.0;

    for (int k = 0; k < 4; ++k)
    {
        // (turned off the axes and the diagonals, so that no regular pattern lines up with the taps and comes through unblurred)
        const float2 off = k == 0 ? float2(1.5, 0.5) : (k == 1 ? float2(-0.5, 1.5) : (k == 2 ? float2(-1.5, -0.5) : float2(0.5, -1.5)));
        float2 uv = (work.origin + clamp(at + off, 0.0, work.size - 1.0) + 0.5) * gWorkSize.zw;
        uv.x = clamp(uv.x, (eye.origin.x + 0.5) * gWorkSize.z, (eye.origin.x + eye.size.x - 0.5) * gWorkSize.z);
        if ((gFlags & F_STEADY) != 0u)
        {
            sum += tKept.SampleLevel(sLinear, uv, 0).rgb;
        }
        else
        {
            const float3 mx = tModel.SampleLevel(sLinear, uv, 0).rgb;
            const float3 px = saturate(tProxy.SampleLevel(sLinear, uv, 0).rgb);
            sum += ((gFlags & F_MODEL_ENCODED) != 0u ? saturate(mx) : LinearToSrgb(mx)) - ((gFlags & F_PROXY_ENCODED) != 0u ? px : LinearToSrgb(px));
        }
    }

    return sum * 0.25;
}

// The sharpening alone, on the plainly enlarged edit: the cheaper of the two (the edit's own two
// taps and the blur's eight, no texel-by-texel reading). With no neighbours read there is nothing
// to hold it within, so what it adds is capped outright instead (gFade.w of an eighth of the range).
float3 EditSharper(Region work, float2 q, float2 dstSize, float3 edit, uint e)
{
    const float2 p = (q + 0.5) * (work.size / dstSize) - 0.5;
    const float cap = gFade.w * 0.125;
    return edit + clamp((edit - EditSoft(work, p, e)) * gFade.z, -cap, cap);
}

float3 EditFollowingRange(Region work, float2 q, float2 dstSize, float3 frameEnc, uint e, out float3 lo, out float3 hi)
{
    const float2 ratio = work.size / dstSize;
    const float2 p = (q + 0.5) * ratio - 0.5;
    const float2 base = floor(p);
    const float2 f = p - base;
    const float2 last = work.size - 1.0;
    const float tight = 1.0 / max(2.0 * gFade.y * gFade.y, 1e-6);
    float3 sum = 0.0, mean = 0.0;
    float total = 0.0;
    lo = 1e9;
    hi = -1e9;

    for (int j = 0; j < 2; ++j)
    {
        for (int i = 0; i < 2; ++i)
        {
            const float2 at = clamp(base + float2((float) i, (float) j), 0.0, last);
            const int3 texel = int3((int2) (work.origin + at), 0);
            const float3 px = saturate(tProxy.Load(texel).rgb);
            const float3 pe = (gFlags & F_PROXY_ENCODED) != 0u ? px : LinearToSrgb(px);
            float3 texelEdit = tKept.Load(texel).rgb;

            if ((gFlags & F_STEADY) == 0u)
            {
                const float3 mx = tModel.Load(texel).rgb;
                texelEdit = ((gFlags & F_MODEL_ENCODED) != 0u ? saturate(mx) : LinearToSrgb(mx)) - pe;
            }
            const float beside = (i == 0 ? 1.0 - f.x : f.x) * (j == 0 ? 1.0 - f.y : f.y);
            const float3 off = pe - frameEnc;
            // (never quite nothing: a pixel unlike all four still gets their plain mix)
            const float w = beside * (exp(-dot(off, off) * tight) + 0.02);

            sum += texelEdit * w;
            total += w;
            mean += texelEdit * 0.25;
            lo = min(lo, texelEdit);
            hi = max(hi, texelEdit);
        }
    }

    float3 edit = sum / max(total, 1e-6);

    // (below nothing it goes the other way, towards its own blur: grain taken out, not put in)
    if (gFade.z != 0.0)
    {
        const float3 room = (hi - lo) * gFade.w;
        edit = clamp(edit + (edit - EditSoft(work, p, e)) * gFade.z, lo - room, hi + room);
    }

    return edit;
}

float3 EditFollowing(Region work, float2 q, float2 dstSize, float3 frameEnc, uint e)
{
    float3 lo, hi;
    return EditFollowingRange(work, q, dstSize, frameEnc, e, lo, hi);
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

        // For seeing where the window is: its edge in a colour nothing in a scene has, and, a
        // little dimmer, the line inside which the model's work lands whole (between the two
        // it fades out).
        if ((gFlags & F_OUTLINE) != 0u)
        {
            const float2 in_ = min(q, window.size - 1.0 - q);
            const float2 beside = float2(1.5, 1.5) / window.size;
            const float least = min(min(WindowWeight((q + 0.5) / window.size + float2(beside.x, 0.0)), WindowWeight((q + 0.5) / window.size - float2(beside.x, 0.0))),
                                    min(WindowWeight((q + 0.5) / window.size + float2(0.0, beside.y)), WindowWeight((q + 0.5) / window.size - float2(0.0, beside.y))));

            if (min(in_.x, in_.y) < 3.0)
                return EmitEncoded(float3(1.0, 0.1, 0.8), frameA);

            if (weight >= 1.0 && least < 1.0)
                return EmitEncoded(float3(0.1, 0.9, 0.9), frameA);
        }
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
    // the frame itself. (Kept over frames, it is in tKept as it stands.)
    float3 edit = modelEnc - proxyEnc;

    if ((gFlags & F_STEADY) != 0u)
    {
        const Region eye = WholeEye(gWorkSize.xy, e);
        float2 uv = (work.origin + (q + 0.5) * (work.size / window.size)) * gWorkSize.zw;
        uv.x = clamp(uv.x, (eye.origin.x + 0.5) * gWorkSize.z, (eye.origin.x + eye.size.x - 0.5) * gWorkSize.z);
        edit = tKept.SampleLevel(sLinear, uv, 0).rgb;

        if (gMode == 1u)
            return EmitEncoded(Mix(frameEnc, saturate(proxyEnc + edit), weight), alpha);
    }

    if ((gFlags & F_FULL) != 0u)
    {
        // gathered at the frame's own size already, enlarged and sharpened as it was gathered
        edit = tKept.Load(int3(d, 0)).rgb;
    }
    else if (work.size.x < window.size.x || work.size.y < window.size.y)
    {
        if ((gFlags & F_FOLLOW) != 0u)
            edit = EditFollowing(work, q, window.size, frameEnc, e);
        else if (gFade.z != 0.0)
            edit = EditSharper(work, q, window.size, edit, e);
    }

    edit *= gStrength * weight;

    if (gMode == 2u)
        return EmitEncoded(saturate(0.5 + edit * 4.0), alpha);

    return EmitEncoded(AddEditInGamut(frameEnc, edit), alpha);
}

// ---- PSSteady ----------------------------------------------------------------------------------
//
// The model's edit, kept from frame to frame at the model's own size.
//
// The model is run afresh on every frame and does not answer quite the same twice: what it adds
// to skin shifts a little from one frame to the next, and at a lower model resolution each of
// its pixels is several of the screen's, so the shifting shows as a crawl. Here its edit (its
// answer minus what it was shown, tModel - tProxy) is blended into the edit as kept a frame ago
// (tKept), which is first carried to where things are now by the motion vectors (tFrame, the
// model-size copy the model itself is given: how far a point has moved since the last frame, in
// eye widths and heights, y up the picture). gStrength is how much of the new edit is taken: at
// 0.2 a still picture's edit is the mean of about the last nine.
//
// What is kept can be wrong for a pixel: something moved in front, or the picture changed. So
// before it is used it is brought within what this frame's edit holds in the three by three
// texels around (and a quarter of that range more) -- stale edit cannot stand where the model
// now says something else, which is what keeps moving things from trailing ghosts. Where the
// place a pixel came from is off the picture, and on the first frame (gMode 1), there is nothing
// kept and the new edit is taken whole.
//
// The motion vectors are the camera's, and of things the game moves as whole objects. A person
// moving by their own animation has none in VaM, and what was kept for the place they were in
// trailed behind them. So with the edit is kept how bright the model's input was there (in the
// alpha), and where the place a texel came from was not as bright as the texel is now -- the
// picture there is no longer the same picture -- less of what was kept is used, down to none at
// a difference of a twelfth of the range.
float4 PSSteady(float4 pos : SV_Position) : SV_Target
{
    const uint e = EyeOf(pos.x, gWorkSize.x);
    const Region work = WholeEye(gWorkSize.xy, e);
    const float2 q = floor(pos.xy) - work.origin;
    const float2 last = work.size - 1.0;
    float3 now = 0.0, lo = 1e9, hi = -1e9;
    float alpha = 0.0;
    float beside[4] = { 0.0, 0.0, 0.0, 0.0 }; // how bright the model's input is to the left, right, above, below

    for (int j = -1; j <= 1; ++j)
    {
        for (int i = -1; i <= 1; ++i)
        {
            const int3 texel = int3((int2) (work.origin + clamp(q + float2((float) i, (float) j), 0.0, last)), 0);
            const float3 m = tModel.Load(texel).rgb;
            const float3 px = saturate(tProxy.Load(texel).rgb);
            const float3 pe = (gFlags & F_PROXY_ENCODED) != 0u ? px : LinearToSrgb(px);
            const float3 one = ((gFlags & F_MODEL_ENCODED) != 0u ? saturate(m) : LinearToSrgb(m)) - pe;
            const float light = dot(pe, float3(0.299, 0.587, 0.114));
            lo = min(lo, one);
            hi = max(hi, one);

            if (i == 0 && j == 0)
            {
                now = one;
                alpha = light;
            }
            else if (j == 0)
            {
                beside[i < 0 ? 0 : 1] = light;
            }
            else if (i == 0)
            {
                beside[j < 0 ? 2 : 3] = light;
            }
        }
    }

    if (gMode == 1u)
        return float4(now, alpha);

    // Where this texel was a frame ago, in the model's raster as it then was.
    const Region guide = WholeEye(gFrameSize.xy, e);
    const float2 at = (q + 0.5) / work.size;
    const float2 moved = tFrame.Load(int3((int2) (guide.origin + clamp(floor(at * guide.size), 0.0, guide.size - 1.0)), 0)).xy *
                         float2(1.0, (gFlags & F_TOP_DOWN) != 0u ? -1.0 : 1.0);
    const float2 was = at - moved / ((gFlags & F_WINDOW) != 0u ? max(gWindow[e].zw, 1e-4) : 1.0);

    if (was.x < 0.0 || was.y < 0.0 || was.x > 1.0 || was.y > 1.0)
        return float4(now, alpha);

    float2 uv = (work.origin + was * work.size) * gWorkSize.zw;
    uv.x = clamp(uv.x, (work.origin.x + 0.5) * gWorkSize.z, (work.origin.x + work.size.x - 0.5) * gWorkSize.z);
    const float3 room = (hi - lo) * 0.25 + 1.0 / 255.0;
    const float4 before = tKept.SampleLevel(sLinear, uv, 0);
    const float3 kept = clamp(before.rgb, lo - room, hi + room);

    // The picture changed here -- or in the texel beside (an edge moving past: the texel it has
    // just left looks much as it did, the next one does not); or what was kept is further from
    // this frame's edit than the model's answer wanders from frame to frame. With a small share
    // of each new frame what such a texel kept would otherwise stay for a second and more, and
    // did: at a twentieth it trailed behind people where at a fifth it had not been seen.
    float differs = abs(before.a - alpha);

    for (int k = 0; k < 4; ++k)
    {
        const float2 step = (k == 0 ? float2(-1.0, 0.0) : (k == 1 ? float2(1.0, 0.0) : (k == 2 ? float2(0.0, -1.0) : float2(0.0, 1.0)))) * gWorkSize.zw;
        differs = max(differs, abs(tKept.SampleLevel(sLinear, uv + step, 0).a - beside[k]));
    }

    const float3 apart = abs(before.rgb - now);
    const float stale = saturate((max(apart.r, max(apart.g, apart.b)) - 0.06) / 0.08);
    const float changed = max(saturate((differs - 0.012) / 0.04), stale);
    return float4(lerp(kept, now, lerp(saturate(gStrength), 1.0, changed)), alpha);
}

// ---- PSGather ----------------------------------------------------------------------------------
//
// Detail built up over frames: the model's edit kept at the FRAME's size.
//
// A model run at half size has one pixel for four of the frame's, and its edit, however it is
// enlarged, has no more in it than that. But its raster need not lie in the same place every
// frame. Shifted by half of one of its pixels each way in turn (PSDown, gPrev[0].xy here, in the
// frame's pixels), over four frames each of the frame's pixels has once been at the very middle
// of a model pixel -- has had, for that frame, a model pixel of its own. So here each frame's
// edit is enlarged with that shift taken into account (along the frame's edges and sharpened, as
// the resolve would), and each of the frame's pixels takes much of it when it lies near a model
// pixel's middle this frame and little when it lies between -- keeping what it had from the frame
// in which it was the one looked at. What is kept is carried by the motion vectors (tMotion) and
// let go where the picture has changed, as in PSSteady; it is held less tightly to this frame's
// values, since what it is for is to differ from them.
//
// A whole frame's worth of history, twice (RGBA16F), and worth it only where things hold still:
// a person moving by their own animation has no motion vectors here and gets this frame's edit.
float3 EditPlain(Region work, float2 q, float2 dstSize, uint e)
{
    const Region eye = WholeEye(gWorkSize.xy, e);
    float2 uv = (work.origin + (q + 0.5) * (work.size / dstSize)) * gWorkSize.zw;
    uv.x = clamp(uv.x, (eye.origin.x + 0.5) * gWorkSize.z, (eye.origin.x + eye.size.x - 0.5) * gWorkSize.z);
    const float3 mx = saturate(tModel.SampleLevel(sLinear, uv, 0).rgb);
    const float3 px = saturate(tProxy.SampleLevel(sLinear, uv, 0).rgb);
    return ((gFlags & F_MODEL_ENCODED) != 0u ? mx : LinearToSrgb(mx)) - ((gFlags & F_PROXY_ENCODED) != 0u ? px : LinearToSrgb(px));
}

float4 PSGather(float4 pos : SV_Position) : SV_Target
{
    const uint2 d = (uint2) pos.xy;
    const float4 frame = tFrame.Load(int3(d, 0));
    const float3 frameEnc = (gFlags & F_FRAME_ENCODED) != 0u ? saturate(frame.rgb) : LinearToSrgb(frame.rgb);
    const float bright = dot(frameEnc, float3(0.299, 0.587, 0.114));
    const uint e = EyeOf(pos.x, gDstSize.x);
    const Region window = WholeEye(gDstSize.xy, e);
    const Region work = WholeEye(gWorkSize.xy, e);
    const float2 q = floor(pos.xy) - window.origin;
    const float2 shifted = q - gPrev[0].xy;

    // this frame's edit here, and what the model's pixels around hold
    float3 lo, hi;
    float3 now = EditFollowingRange(work, shifted, window.size, frameEnc, e, lo, hi);

    if ((gFlags & F_FOLLOW) == 0u)
    {
        now = EditPlain(work, shifted, window.size, e);

        if (gFade.z != 0.0)
            now = EditSharper(work, shifted, window.size, now, e);
    }

    if (gMode == 1u)
        return float4(now, bright);

    // how near the middle of a model pixel this pixel lies this frame: 1 at it, 0 half-way to the next
    const float2 p = (shifted + 0.5) * (work.size / window.size) - 0.5;
    const float2 off = abs(p - round(p)) * 2.0;
    const float near_ = saturate(1.0 - off.x) * saturate(1.0 - off.y);

    // where it was a frame ago
    const Region guide = WholeEye(gPrev[1].xy, e);
    const float2 at = (q + 0.5) / window.size;
    const float2 moved = tMotion.Load(int3((int2) (guide.origin + clamp(floor(at * guide.size), 0.0, guide.size - 1.0)), 0)).xy *
                         float2(1.0, (gFlags & F_TOP_DOWN) != 0u ? -1.0 : 1.0);
    const float2 was = at - moved;

    if (was.x < 0.0 || was.y < 0.0 || was.x > 1.0 || was.y > 1.0)
        return float4(now, bright);

    float2 uv = (window.origin + was * window.size) * gDstSize.zw;
    uv.x = clamp(uv.x, (window.origin.x + 0.5) * gDstSize.z, (window.origin.x + window.size.x - 0.5) * gDstSize.z);
    const float4 before = tKept.SampleLevel(sLinear, uv, 0);

    // Has the picture changed here -- or right beside here? A person's edge moving past leaves the
    // pixels it has just left looking, each on its own, much as they did; the pixel two along has
    // changed outright. And skin sliding over skin changes no brightness at all, but what was kept
    // for it then differs from this frame's edit by more than fine detail ever does. Either way
    // what was kept is stale: it trailed behind people until both were looked for (and until a
    // pixel that keeps to its own look was made to give way to them).
    float differs = abs(before.a - bright);

    for (int k = 0; k < 4; ++k)
    {
        const float2 step = k == 0 ? float2(2.0, 0.0) : (k == 1 ? float2(-2.0, 0.0) : (k == 2 ? float2(0.0, 2.0) : float2(0.0, -2.0)));
        const float3 f = tFrame.Load(int3((int2) (window.origin + clamp(q + step, 0.0, window.size - 1.0)), 0)).rgb;
        const float3 fe = (gFlags & F_FRAME_ENCODED) != 0u ? saturate(f) : LinearToSrgb(f);
        differs = max(differs, abs(tKept.SampleLevel(sLinear, uv + step * gDstSize.zw, 0).a - dot(fe, float3(0.299, 0.587, 0.114))));
    }

    const float3 apart = abs(before.rgb - now);
    // (A stricter guard -- eight taps, a change counted from two levels -- took the last of the trails and
    // made the picture flicker: wherever what was kept is let go, a pixel shows this frame's edit, and with
    // the raster shifted every frame that differs a little each time. This is the one before it.)
    const float stale = saturate((max(apart.r, max(apart.g, apart.b)) - 0.10) / 0.10);
    const float changed = max(saturate((differs - 0.02) / 0.05), stale);
    const float3 room = lerp((hi - lo) + 0.16, (hi - lo) * 0.25 + 1.0 / 255.0, changed);
    const float3 kept = clamp(before.rgb, lo - room, hi + room);
    // (gPrev[0].z: how much a pixel keeps to its own look -- at 1 it takes all of the edit when it
    // is at a model pixel's middle and nothing of it when it is half-way to the next)
    const float own = saturate(gPrev[0].z);
    const float take = saturate(lerp(lerp(0.15, 0.03, own), lerp(0.3, 1.0, own), near_) * gStrength * 5.0);
    return float4(lerp(kept, now, lerp(take, 1.0, changed)), bright);
}

// ---- PSSharpen ---------------------------------------------------------------------------------
//
// Contrast-adaptive sharpening of a finished frame, after the shape of FidelityFX RCAS: the
// centre is pushed away from its four neighbours by a negative lobe, and the lobe is held to
// what keeps the result between the darkest and the brightest of the five. Flat areas and hard
// edges are left nearly alone; soft detail gains the most.
//
// tFrame is a copy of the target. Each eye of a double-wide frame is filtered on its own.

float3 Squash(float3 c)
{
    c = max(c, 0.0);
    return c / (1.0 + max(c.r, max(c.g, c.b)));
}

float3 Unsquash(float3 c)
{
    c = clamp(c, 0.0, 0.999);
    return c / (1.0 - max(c.r, max(c.g, c.b)));
}

float3 SharpenTap(int2 p, bool squash)
{
    float3 c = tFrame.Load(int3(p, 0)).rgb;
    return squash ? Squash(c) : saturate(c);
}

float4 PSSharpen(float4 pos : SV_Position) : SV_Target
{
    const bool squash = (gFlags & F_SQUASH) != 0u;
    const int2 p = int2(pos.xy);
    const int w = (int) gFrameSize.x;
    const int h = (int) gFrameSize.y;
    const int eyeW = gEyes == 2u ? w / 2 : w;
    const int x0 = (gEyes == 2u && p.x >= eyeW) ? eyeW : 0;
    const int x1 = x0 + eyeW - 1;

    const float4 centre = tFrame.Load(int3(p, 0));
    const float3 e = squash ? Squash(centre.rgb) : saturate(centre.rgb);
    const float3 b = SharpenTap(int2(p.x, max(p.y - 1, 0)), squash);
    const float3 d = SharpenTap(int2(max(p.x - 1, x0), p.y), squash);
    const float3 f = SharpenTap(int2(min(p.x + 1, x1), p.y), squash);
    const float3 hh = SharpenTap(int2(p.x, min(p.y + 1, h - 1)), squash);

    const float3 mn4 = min(min(b, d), min(f, hh));
    const float3 mx4 = max(max(b, d), max(f, hh));

    const float3 hitMin = min(mn4, e) / max(4.0 * mx4, 1e-5);
    const float3 hitMax = (1.0 - max(mx4, e)) / min(4.0 * mn4 - 4.0, -1e-5);
    const float3 lobeRgb = max(-hitMin, hitMax);
    const float lobe = max(-0.1875, min(max(lobeRgb.r, max(lobeRgb.g, lobeRgb.b)), 0.0)) * saturate(gStrength);

    float3 o = (lobe * (b + d + f + hh) + e) / (4.0 * lobe + 1.0);
    o = squash ? Unsquash(o) : saturate(o);

    return float4(o, centre.a);
}

// ---- PSDepthFill -------------------------------------------------------------------------------
//
// The scene's depth, laid under what is drawn after the frame is finished. Scene interface atoms
// (a button on a wall) are kept out of the frame the networks see and drawn onto the finished
// picture, whose depth buffer knows nothing of the scene: without this they would show through
// anyone standing in front of them. tFrame holds the scene's depth as the card made it, one number
// a texel, at whatever size the scene was rendered; it is written across the viewport in force
// (gPrev[0]: xy its corner, zw its size, in pixels) from the part of the texture gWindow[0] names
// (one eye of a double-wide one), turned over where the target lies the other way (F_TOP_DOWN).
//
// Read with the bilinear filter: a card's depth is linear across the screen for a flat surface, so
// between texels this is exact on a wall and a ramp one texel wide at a silhouette. Then pushed
// back by gStrength of its distance, so that a panel lying close on a surface is not cut into
// stripes by the little that the scene's sub-pixel jitter moves the texels (gMode 1: nearer is the
// larger number, as it is on this card).

float4 PSDepthFill(float4 pos : SV_Position, out float depth : SV_Depth) : SV_Target
{
    float2 uv = (pos.xy - gPrev[0].xy) / max(gPrev[0].zw, 1.0);

    if ((gFlags & F_TOP_DOWN) != 0u)
        uv.y = 1.0 - uv.y;

    const float2 lo = gWindow[0].xy + 0.5 * gFrameSize.zw;
    const float2 hi = gWindow[0].xy + gWindow[0].zw - 0.5 * gFrameSize.zw;
    const float d = tFrame.SampleLevel(sLinear, clamp(gWindow[0].xy + uv * gWindow[0].zw, lo, hi), 0).r;

    depth = gMode == 1u ? d * (1.0 - gStrength) : d + (1.0 - d) * gStrength;
    return float4(0.0, 0.0, 0.0, 0.0);
}

// ---- PSPassthrough -----------------------------------------------------------------------------
//
// Where a finished frame holds the key colour, the room instead: for each such pixel, the point
// the eye sees through it at a set distance, carried into the camera's space and through the
// camera's fisheye to a place in its frame. tFrame is a copy of the target, tProxy the camera's
// frame (grey; the two lenses side by side). One eye per draw.

// ---- the camera picture's look ------------------------------------------------------------------
//
// The cameras are monochrome. What the room is shown in: the grey as it comes (0); a ramp of
// three colours laid over it -- night-vision green, amber, sepia... (1); a ramp of heat (2); or
// the colours a network guessed for the picture (3: see vws_colour.h), which come a few times a
// second and at a fraction of the picture's size, as two differences from the brightness. The
// brightness stays the camera's own. All of it in displayed (sRGB-encoded) values.

// (Dave Hoskins' hash without sine)
float Speck(float2 p, float seed)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031 + seed * 0.0137);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float3 Heat(float t)
{
    static const float3 stops[6] =
    {
        float3(0.0, 0.0, 0.03), float3(0.33, 0.0, 0.5), float3(0.82, 0.08, 0.3), float3(1.0, 0.5, 0.0), float3(1.0, 0.9, 0.2), float3(1.0, 1.0, 1.0)
    };
    const float s = saturate(t) * 5.0;
    const int i = min((int) s, 4);
    return lerp(stops[i], stops[i + 1], s - (float) i);
}

// A point in the space of the camera the colours belong to: where in tColour, and how far that is
// to be believed (the lens's picture is a circle, and fades out towards its rim).
float3 ColourAt(float3 q, float focal, float4 k, float2 centre, float lensW, float lensH)
{
    const float across = max(length(q.xy), 1e-9);
    const float angle = atan2(across, -q.z);
    const float a2 = angle * angle;
    const float radius = focal * angle * (1.0 + a2 * (k.x + a2 * (k.y + a2 * (k.z + a2 * k.w))));
    const float2 texel = centre + radius * float2(q.x, -q.y) / across;
    return float3(texel.x / lensW, texel.y / lensH, saturate((lensW * 0.5 - 6.0 - radius) / 60.0));
}

// low, mid, high: the ramp's colours from dark to bright; low.a = which look, mid.a = how much
// grain, high.a = how much darker towards the rim of the view. more: x changes with every
// picture, y = how strong the network's colours are. pixel: where on the target. tangent: how far
// off the eye's axis this line of sight is. colour: tColour there (xy), and how far to believe it.
float3 Look(float grey, float4 low, float4 mid, float4 high, float4 more, float2 pixel, float tangent, float3 colour)
{
    const int look = (int) (low.a + 0.5);

    if (look == 0)
        return grey.xxx;

    float g = saturate(grey + (Speck(pixel, more.x) - 0.5) * mid.a * 0.5);
    g *= 1.0 - high.a * smoothstep(0.45, 1.3, tangent);

    if (look == 1)
        return g < 0.5 ? lerp(low.rgb, mid.rgb, g * 2.0) : lerp(mid.rgb, high.rgb, g * 2.0 - 1.0);

    if (look == 2)
        return Heat(g);

    const float2 d = (tColour.SampleLevel(sLinear, colour.xy, 0).rg * 255.0 - 128.0) / 255.0 * (more.y * colour.z);
    return saturate(float3(g + 1.402 * d.y, g - 0.344136 * d.x - 0.714136 * d.y, g + 1.772 * d.x));
}

cbuffer PassthroughParams : register(b1)
{
    float4 pDst;     // target width, height, 1/width, 1/height
    float4 pKey;     // rgb as displayed (sRGB-encoded), a = tolerance
    float4 pTune;    // softness, camera gain, distance (m), fisheye pixels per radian
    float4 pK;       // the fisheye's polynomial
    float4 pCentre;  // its centre in each lens's half, pixels: left xy, right zw
    float4 pCam;     // camera frame width, height, lens width, view (0 picture, 1 matte, 2 camera everywhere)
    float4 pTan;     // this eye's left, right, top, bottom tangents (y down)
    float4 pRow0;    // a point in the eye's space -> the camera's
    float4 pRow1;
    float4 pRow2;
    float4 pMisc;    // eye, eyes in the target, target holds encoded values, first row at the top
    float4 pLow;     // the look (see Look): the ramp's dark end; a = which look
    float4 pMid;     // its middle; a = grain
    float4 pHigh;    // its bright end; a = darker towards the rim
    float4 pMore;    // x = changes with every picture, y = the network's colours' strength (0 where there are none yet)
    float4 pCol0;    // a point in the eye's space -> the camera the colours belong to, as it stood then
    float4 pCol1;
    float4 pCol2;
};

// The alpha the game's own picture is marked with while the room's depth is in use. The interface is
// drawn over the frame afterwards with ordinary blending, which leaves a^2 + mark * (1 - a) in the
// alpha for a pixel of opacity a: anything above the mark is interface. The mark is low so that
// this holds for a see-through panel too -- at a half, a panel under about 85% opacity stayed
// below the old threshold, was taken for scene with nothing near behind it, and the room was drawn
// over it everywhere but in front of the person.
static const float kOwn = 32.0 / 255.0;

float4 PSPassthrough(float4 pos : SV_Position) : SV_Target
{
    const float4 c = tFrame.Load(int3(int2(pos.xy), 0));
    const bool encoded = pMisc.z > 0.5;

    // With an overlay to be cut by this frame, its alpha is made to say where the room went: 1 for
    // the game's own picture, 0 for the room.
    // With the room's depth in use the game's own picture is marked with kOwn: what is drawn
    // over the frame afterwards -- the interface -- raises that, and can so be told from it.
    const bool halved = pCam.w >= 15.5;
    const float asked = halved ? pCam.w - 16.0 : pCam.w;
    const bool mark = asked >= 7.5;
    const int view = (int) (mark ? asked - 8.0 : asked);
    const float own = halved ? kOwn : 1.0;

    const float3 seen = encoded ? saturate(c.rgb) : LinearToSrgb(saturate(c.rgb));
    float matte = 1.0 - smoothstep(pKey.a, pKey.a + max(pTune.x, 1e-4), length(seen - pKey.rgb));

    if (view == 2)
        matte = 1.0;

    if (matte <= 0.0)
        return float4(c.rgb, mark ? own : c.a);

    if (view == 1)
        return float4(encoded ? matte.xxx : SrgbToLinear(matte.xxx), mark ? own * (1.0 - matte) : c.a);

    // Where this pixel is in its eye's picture, from the top left.
    const bool wide = pMisc.y > 1.5;
    const float eyeW = wide ? pDst.x * 0.5 : pDst.x;
    const float u = (pos.x - (wide ? pMisc.x * eyeW : 0.0)) / eyeW;
    const float fromTop = pos.y * pDst.w;
    const float v = pMisc.w > 0.5 ? fromTop : 1.0 - fromTop;

    // The eye looks down -z; its tangents count y downwards.
    const float3 ray = normalize(float3(lerp(pTan.x, pTan.y, u), -lerp(pTan.z, pTan.w, v), -1.0));
    const float3 at = ray * pTune.z;
    const float3 q = float3(dot(pRow0.xyz, at) + pRow0.w, dot(pRow1.xyz, at) + pRow1.w, dot(pRow2.xyz, at) + pRow2.w);

    // The camera looks down -z too. Its lens lays an angle from the axis down as a radius.
    const float across = max(length(q.xy), 1e-9);
    const float angle = atan2(across, -q.z);
    const float a2 = angle * angle;
    const float radius = pTune.w * angle * (1.0 + a2 * (pK.x + a2 * (pK.y + a2 * (pK.z + a2 * pK.w))));

    const float lensW = pCam.z;
    const float2 centre = pMisc.x < 0.5 ? pCentre.xy : pCentre.zw;
    float2 texel = centre + radius * float2(q.x, -q.y) / across;

    // The lens's picture is a circle; beyond it there is nothing, and it fades out towards that.
    const float inside = saturate((lensW * 0.5 - 6.0 - radius) / 40.0);
    texel = clamp(texel, 0.0, float2(lensW, pCam.y) - 1.0);

    const float2 uv = float2((texel.x + 0.5 + pMisc.x * lensW) / pCam.x, (texel.y + 0.5) / pCam.y);
    const float grey = saturate(tProxy.SampleLevel(sLinear, uv, 0).r * pTune.y);
    const float3 there = float3(dot(pCol0.xyz, at) + pCol0.w, dot(pCol1.xyz, at) + pCol1.w, dot(pCol2.xyz, at) + pCol2.w);
    const float3 shown = Look(grey, pLow, pMid, pHigh, pMore, pos.xy, length(float2(lerp(pTan.x, pTan.y, u), lerp(pTan.z, pTan.w, v))),
                              ColourAt(there, pTune.w, pK, pCentre.xy, lensW, pCam.y)) * inside;
    const float3 room = encoded ? shown : SrgbToLinear(shown);

    return float4(lerp(c.rgb, room, matte), mark ? own * (1.0 - matte) : c.a);
}

// ---- PSMatte -----------------------------------------------------------------------------------
//
// How much of each pixel of a finished frame is the key colour, written into one eye's half of a
// matte the overlay is cut by. The matte's first row is the top of the picture whichever way up
// the frame lies. tFrame is the frame itself.

float4 PSMatte(float4 pos : SV_Position) : SV_Target
{
    // Below the matte, a row of texels that carries the head's pose the frame was drawn from, each
    // number spread over three bytes. Written by the same pass as the matte, it reaches the
    // overlay's device at the same moment the matte does -- which a pose handed across on the
    // side would not: the card runs a frame behind the commands it is sent.
    if (pCam.w > 1.5)
    {
        const int i = (int) pos.x;

        // After the pose: whether it is known, and whether the matte carries the scene's depth.
        if (i >= 12)
        {
            const float flag = i == 13 ? pK.x : pMisc.x;
            return float4(flag, flag, flag, 1.0);
        }

        const float4 rows[3] = { pRow0, pRow1, pRow2 };
        const float n = round(saturate(rows[i / 4][i % 4] * 0.5 + 0.5) * 16777215.0);
        const float hi = floor(n / 65536.0);
        const float mid = floor((n - hi * 65536.0) / 256.0);
        const float lo = n - hi * 65536.0 - mid * 256.0;
        return float4(hi / 255.0, mid / 255.0, lo / 255.0, 1.0);
    }

    const float halfW = pDst.x * 0.5;
    const float u = (pos.x - pMisc.x * halfW) / halfW;
    const float v = pos.y * pDst.w;

    const float2 at = float2(pMisc.y > 1.5 ? (pMisc.x + u) * 0.5 : u, pMisc.w > 0.5 ? v : 1.0 - v);
    const float4 c = tFrame.SampleLevel(sLinear, at, 0);
    const float3 seen = pMisc.z > 0.5 ? saturate(c.rgb) : LinearToSrgb(saturate(c.rgb));
    float matte = 1.0 - smoothstep(pKey.a, pKey.a + max(pTune.x, 1e-4), length(seen - pKey.rgb));

    // Where the room has been drawn into the frame already, its alpha says so (see PSPassthrough).
    // (pCentre.x: the game's own picture was marked with kOwn, and more than that is the
    // interface, drawn over the frame since.)
    bool menu = false;

    if (pCam.w > 0.5)
    {
        const bool halved = pCentre.x > 0.5;
        // The room pass left 0 where it put the room and 1 (kOwn, with the room's depth in use)
        // on the game's own picture. The interface, drawn over the frame since with ordinary
        // blending, leaves only a^2 of its opacity a in the alpha over the room: read as it
        // stands, a half-transparent panel let three quarters of the overlay through and was all
        // but gone -- except in front of the person, where the alpha was 1 to begin with. The
        // frame under the interface is right as it is (the room is in it, the interface over it),
        // so a little alpha is taken for the whole of it: the overlay is cut away there.
        matte = 1.0 - saturate(c.a / kOwn);
        menu = halved && c.a > kOwn + 4.0 / 255.0;
    }

    // Beside the matte, how near the scene is along this line of sight: one over its distance in
    // metres, four per metre being the most that fits. The overlay sets the room's own against it.
    // tProxy is the scene's depth buffer as the card keeps it (pK: there is one, its first row is
    // the top, it holds both eyes, it runs from 1 at the near plane to 0 at the far one).
    float nearness = 0.0;

    if (pK.x > 0.5)
    {
        uint dw, dh;
        tProxy.GetDimensions(dw, dh);
        const float2 where = float2(pK.z > 1.5 ? (pMisc.x + u) * 0.5 : u, pK.y > 0.5 ? v : 1.0 - v);
        const float stored = tProxy.Load(int3(min((int2) (where * float2(dw, dh)), int2(dw, dh) - 1), 0)).r;
        const float closest = pTune.y, farthest = pTune.z;
        const float z = pK.w > 0.5 ? closest * farthest / (closest + stored * (farthest - closest))
                                   : closest * farthest / (farthest - stored * (farthest - closest));
        const float2 t = float2(lerp(pTan.x, pTan.y, u), lerp(pTan.z, pTan.w, v));
        nearness = saturate(0.25 * max(pTune.w, 1e-6) / (z * sqrt(1.0 + dot(t, t))));
    }

    // Nothing of the room comes before the interface: the depth buffer knows nothing of it.
    if (menu)
        nearness = 1.0;

    return float4(matte, nearness, matte, 1.0);
}

// ---- PSOverlay ---------------------------------------------------------------------------------
//
// The room on a quad that stands in front of where the head was when the camera took its frame:
// one picture per eye, side by side. Each texel is a point on the quad; the eye's line to it is
// followed out to where the room is taken to be, and that point is found in the camera's frame
// (tProxy) the way PSPassthrough finds it. How much of it shows is the game's matte (tModel),
// looked up in the direction the game's own picture has that line of sight.

cbuffer OverlayParams : register(b1)
{
    float4 oQuad;       // half-width, half-height, distance of the quad; distance of the room
    float4 oLens;       // fisheye pixels per radian, gain, lens width, view
    float4 oK;
    float4 oCentre;
    float4 oCam;        // camera frame width, height, matte usable, the right eye's x in the head
    float4 oTan[2];     // each eye's left, right, top, bottom tangents in the game's picture (y down)
    float4 oCamRows[6]; // the head's space -> each camera's, three rows apiece
    float4 oRot[3];     // the head at the camera frame's moment, in the room (rotation); [0].w = it is known
    float4 oOut;        // texture width, height, 1/width, 1/height
    float4 oMatte;      // x = how much of the matte texture's height is matte, y = the row its pose is in, z = how far the depth grid reaches to each side, as a tangent
    float4 oDepth;      // x = the room's depth is used, y = how much nearer (per metre) the room must be than the scene to show in front of it, z = over how much more it fades in
    float4 oGrid;       // the depth grid: cells per side, -, -, the most 1/distance
    float4 oLow;        // the look (see Look): the ramp's dark end; a = which look
    float4 oMid;        // its middle; a = grain
    float4 oHigh;       // its bright end; a = darker towards the rim
    float4 oMore;       // x = changes with every picture, y = the network's colours' strength (0 where there are none yet)
    float4 oCol[3];     // a point in the head's space, as it stood at the camera frame's moment -> the camera the colours belong to, as it stood then
};

// A point of the head's space in a lens's picture: its texel, and how far from the lens's centre
// that is. The camera looks down -z; its lens lays an angle from the axis down as a radius.
float3 LensTexel(float3 p, int lens)
{
    const float4 r0 = oCamRows[lens * 3], r1 = oCamRows[lens * 3 + 1], r2 = oCamRows[lens * 3 + 2];
    const float3 q = float3(dot(r0.xyz, p) + r0.w, dot(r1.xyz, p) + r1.w, dot(r2.xyz, p) + r2.w);
    const float across = max(length(q.xy), 1e-9);
    const float angle = atan2(across, -q.z);
    const float a2 = angle * angle;
    const float radius = oLens.x * angle * (1.0 + a2 * (oK.x + a2 * (oK.y + a2 * (oK.z + a2 * oK.w))));
    const float2 centre = lens == 0 ? oCentre.xy : oCentre.zw;
    return float3(centre + radius * float2(q.x, -q.y) / across, radius);
}

// How near the room is along a line of sight from the middle of the head: one over its distance,
// per metre; 0 where it is not known. tFrame is the depth grid, worked out on the processor from
// the two lenses (see vws_depth.h) -- its blue is the value to use.
float GridNearness(float3 dir)
{
    if (dir.z > -1e-3)
        return 0.0;

    const float2 uv = float2(dir.x, -dir.y) / (-dir.z * oMatte.z) * 0.5 + 0.5;

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return 0.0;

    return tFrame.SampleLevel(sLinear, uv, 0).b * oGrid.w;
}

// The same along an eye's line of sight: the eye is beside the middle of the head, so the point
// found is looked up once more from there.
float RoomNearness(float3 from, float3 ray)
{
    const float first = GridNearness(ray);
    return first > 0.0 ? GridNearness(normalize(from + ray / first)) : 0.0;
}

struct OverlayVertex
{
    float4 pos : SV_Position;
    nointerpolation float3 turn0 : TEXCOORD0; // the head at the camera frame's moment -> the head
    nointerpolation float3 turn1 : TEXCOORD1; // the game's frame was drawn from, row by row
    nointerpolation float3 turn2 : TEXCOORD2;
};

float PoseNumber(int i)
{
    const float3 t = round(tModel.Load(int3(i, (int) oMatte.y, 0)).rgb * 255.0);
    return dot(t, float3(65536.0, 256.0, 1.0)) / 16777215.0 * 2.0 - 1.0;
}

// The turn is worked out once, here, from the pose the game wrote beside its matte.
OverlayVertex VSOverlay(uint id : SV_VertexID)
{
    OverlayVertex o;
    const float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);

    float3x3 turn = float3x3(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);

    if (oCam.z > 0.5 && oRot[0].w > 0.5 && tModel.Load(int3(12, (int) oMatte.y, 0)).r > 0.5)
    {
        const float3x3 game = float3x3(PoseNumber(0), PoseNumber(1), PoseNumber(2), PoseNumber(4), PoseNumber(5), PoseNumber(6), PoseNumber(8),
                                       PoseNumber(9), PoseNumber(10));
        const float3x3 then = float3x3(oRot[0].xyz, oRot[1].xyz, oRot[2].xyz);
        turn = mul(transpose(game), then);
    }

    o.turn0 = turn[0];
    o.turn1 = turn[1];
    o.turn2 = turn[2];
    return o;
}

float4 PSOverlay(OverlayVertex vertex) : SV_Target
{
    const float4 pos = vertex.pos;
    const float eyeW = oOut.x * 0.5;
    const int eye = pos.x >= eyeW ? 1 : 0;
    const float u = (pos.x - eye * eyeW) / eyeW;
    const float v = pos.y * oOut.w;

    const float3 onQuad = float3((2.0 * u - 1.0) * oQuad.x, (1.0 - 2.0 * v) * oQuad.y, -oQuad.z);
    const float3 from = float3(eye == 0 ? -oCam.w : oCam.w, 0.0, 0.0);
    const float3 ray = normalize(onQuad - from);
    const float lensW = oLens.z;
    const int view = (int) oLens.w;

    const float3 shown = from + ray * oQuad.w;
    const float3 lens = LensTexel(shown, eye);
    const float inside = saturate((lensW * 0.5 - 6.0 - lens.z) / 40.0);
    const float2 texel = clamp(lens.xy, 0.0, float2(lensW, oCam.y) - 1.0);
    const float grey = saturate(tProxy.SampleLevel(sLinear, float2((texel.x + 0.5 + eye * lensW) / oCam.x, (texel.y + 0.5) / oCam.y), 0).r * oLens.y);

    // The same line of sight in the game's picture.
    float alpha = 0.0, scene = 0.0;
    bool inPicture = false;
    const float3 g = float3(dot(vertex.turn0, ray), dot(vertex.turn1, ray), dot(vertex.turn2, ray));

    if (oCam.z > 0.5 && g.z < -1e-3)
    {
        const float4 tn = oTan[eye];
        const float2 m = float2((g.x / -g.z - tn.x) / (tn.y - tn.x), (-g.y / -g.z - tn.z) / (tn.w - tn.z));

        if (m.x >= 0.0 && m.x <= 1.0 && m.y >= 0.0 && m.y <= 1.0)
        {
            const float4 cut = tModel.SampleLevel(sLinear, float2((eye + m.x) * 0.5, m.y * oMatte.x), 0);
            alpha = cut.r;
            scene = cut.g * 4.0;
            inPicture = true;
        }
    }

    // Where the room is nearer than what the game drew there, the room is in front: a hand held
    // out before a figure that stands further off covers it, and one behind it does not.
    //
    // The picture itself is left as it is. What this texel shows is whatever its lens saw along
    // the lens's own line to the point the room is taken to be at -- wherever along that line the
    // thing really is -- so that is the line the room's depth is asked along: the cut then falls
    // on the hand as it is pictured, in each eye, and not beside it.
    float room = 0.0;

    if (oDepth.x > 0.5 && (view == 3 || (inPicture && alpha < 1.0)))
    {
        const float4 r0 = oCamRows[eye * 3], r1 = oCamRows[eye * 3 + 1], r2 = oCamRows[eye * 3 + 2];
        const float3 lensAt = -(r0.xyz * r0.w + r1.xyz * r1.w + r2.xyz * r2.w);
        room = RoomNearness(lensAt, normalize(shown - lensAt));

        if (inPicture && room > 0.0 && tModel.Load(int3(13, (int) oMatte.y, 0)).r > 0.5)
            alpha += (1.0 - alpha) * smoothstep(oDepth.y, oDepth.y + max(oDepth.z, 1e-4), room - scene);
    }

    if (view == 2)
        alpha = 1.0;

    // For looking at what the depth is taken to be: the room's, then the scene's (near is bright).
    if (view == 3)
        return float4(sqrt(saturate(room / oGrid.w)).xxx, 1.0);

    if (view == 4)
        return float4(sqrt(saturate(scene * 0.25)).xxx, inPicture ? 1.0 : 0.0);

    const float3 there = float3(dot(oCol[0].xyz, shown) + oCol[0].w, dot(oCol[1].xyz, shown) + oCol[1].w, dot(oCol[2].xyz, shown) + oCol[2].w);
    return float4(Look(grey, oLow, oMid, oHigh, oMore, pos.xy, g.z < -1e-3 ? length(g.xy) / -g.z : 4.0,
                       ColourAt(there, oLens.x, oK, oCentre.xy, lensW, oCam.y)), alpha * inside);
}
