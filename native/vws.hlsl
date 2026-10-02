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
// The 8-bit surfaces hold sRGB-encoded values and are read and written RAW (UNORM views), with the
// conversion done here. That is deliberate for the resolve: the edit is a difference of encoded
// values, and filtering the encoded values keeps the enlargement linear in the edit -- interp(m) -
// interp(p) is interp(m - p) -- where filtering decoded values and re-encoding is not. The *_ENCODED
// flags say which surfaces arrive that way; a surface Unity typed as *_SRGB is converted by the
// hardware instead and arrives linear.

cbuffer Params : register(b0)
{
    float4 gFrameSize;  // xy = frame size in pixels, zw = 1/size
    float4 gDstSize;    // xy = this pass's render target size, zw = 1/size
    float4 gWorkSize;   // xy = model raster size, zw = 1/size
    uint   gEyes;       // 1, or 2 for a double-wide stereo frame
    uint   gFlags;
    float  gStrength;   // resolve: how much of the edit lands (1 = all of it)
    uint   gMode;       // resolve: 0 matched residual, 1 classic, 2 show the edit, 3 frame untouched
};

static const uint F_FRAME_ENCODED = 1u; // the frame's values are sRGB-encoded as sampled
static const uint F_PROXY_ENCODED = 2u; // likewise the model input's
static const uint F_MODEL_ENCODED = 4u; // likewise the model output's
static const uint F_OUT_ENCODED   = 8u; // the render target stores what is written, unconverted

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

// In a double-wide stereo frame the two eyes sit side by side. A bilinear tap near the middle would
// read the other eye's edge texel; each tap is confined to the half its destination pixel is in.
float2 EyeClamp(float2 uv, float2 invTexSize)
{
    if (gEyes == 2u)
    {
        float base = uv.x < 0.5 ? 0.0 : 0.5;
        uv.x = clamp(uv.x, base + 0.5 * invTexSize.x, base + 0.5 - 0.5 * invTexSize.x);
    }

    return uv;
}

// The exact box resample: integrate the texture over the footprint of destination pixel `d`.
// `ratio` is texels per destination pixel (> 1 on at least one axis). Eye halves never mix: the
// footprints tile each half exactly because both rasters split at their own midpoint.
float4 AreaAverage(Texture2D<float4> t, float2 texSize, uint2 d, float2 ratio, bool decode)
{
    const float x0 = (float) d.x * ratio.x;
    const float x1 = x0 + ratio.x;
    const float y0 = (float) d.y * ratio.y;
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

// One read of a texture at this destination pixel, whatever its size relative to the target: a load
// at 1:1, an area average when the texture is larger, a (per-eye) bilinear tap when it is smaller.
// `decode` converts each texel from sRGB to linear BEFORE it is filtered; without it the stored
// values are filtered as they are.
float4 Fetch(Texture2D<float4> t, float4 texSize, float4 pos, bool decode)
{
    const uint2 d = (uint2) pos.xy;
    const float2 ratio = texSize.xy * gDstSize.zw;
    float4 result = 0.0;

    if (texSize.x == gDstSize.x && texSize.y == gDstSize.y)
    {
        result = Texel(t.Load(int3(d, 0)), decode);
    }
    else if (ratio.x > 1.0 || ratio.y > 1.0)
    {
        result = AreaAverage(t, texSize.xy, d, ratio, decode);
    }
    else
    {
        // Enlarging: the hardware filters whatever is stored, so a decode here comes after the
        // filter. Only an 8-bit frame being supersampled takes that route, and the difference is
        // immaterial.
        const float2 uv = EyeClamp(pos.xy * gDstSize.zw, texSize.zw);
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

    // Averaged in linear light, as any minification should be.
    const float4 c = Fetch(tFrame, gFrameSize, pos, (gFlags & F_FRAME_ENCODED) != 0u);
    return float4((gFlags & F_OUT_ENCODED) != 0u ? LinearToSrgb(c.rgb) : c.rgb, c.a);
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

float4 PSResolve(float4 pos : SV_Position) : SV_Target
{
    const uint2 d = (uint2) pos.xy;
    const float4 frame = tFrame.Load(int3(d, 0));
    const float3 frameEnc = (gFlags & F_FRAME_ENCODED) != 0u ? saturate(frame.rgb) : LinearToSrgb(frame.rgb);

    if (gMode == 3u)
        return EmitEncoded(frameEnc, saturate(frame.a));

    const float4 proxy = Fetch(tProxy, gWorkSize, pos, false);
    const float4 model = Fetch(tModel, gWorkSize, pos, false);
    const float3 proxyEnc = (gFlags & F_PROXY_ENCODED) != 0u ? proxy.rgb : LinearToSrgb(proxy.rgb);
    const float3 modelEnc = (gFlags & F_MODEL_ENCODED) != 0u ? model.rgb : LinearToSrgb(model.rgb);

    // Classic: the model's own picture, enlarged. What a plain upscale of the output would give.
    if (gMode == 1u)
        return EmitEncoded(modelEnc, model.a);

    // The edit. Exactly zero wherever the model left its input alone, so those pixels come back as
    // the frame itself.
    const float3 edit = (modelEnc - proxyEnc) * gStrength;

    if (gMode == 2u)
        return EmitEncoded(saturate(0.5 + edit * 4.0), model.a);

    return EmitEncoded(AddEditInGamut(frameEnc, edit), model.a);
}
