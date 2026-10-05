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
static const uint F_SQUASH        = 128u; // sharpen: the frame is scene light with no ceiling; bring it under 1 for the filter

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

// ---- PSPassthrough -----------------------------------------------------------------------------
//
// Where a finished frame holds the key colour, the room instead: for each such pixel, the point
// the eye sees through it at a set distance, carried into the camera's space and through the
// camera's fisheye to a place in its frame. tFrame is a copy of the target, tProxy the camera's
// frame (grey; the two lenses side by side). One eye per draw.

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
    const float grey = saturate(tProxy.SampleLevel(sLinear, uv, 0).r * pTune.y) * inside;
    const float3 room = encoded ? grey.xxx : SrgbToLinear(grey.xxx);

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

    return float4(grey, grey, grey, alpha * inside);
}
