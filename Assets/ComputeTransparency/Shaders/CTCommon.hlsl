#ifndef COMPUTE_TRANSPARENCY_COMMON_INCLUDED
#define COMPUTE_TRANSPARENCY_COMMON_INCLUDED

// Tile size in pixels is a setting on the renderer feature, not a constant here. One thread
// group owns one tile and one thread owns one pixel, so the group size has to be baked in at
// compile time: the raster kernel exists once per supported size (CSRaster4 .. CSRaster32) and
// the feature picks one. Everything else takes the size as the _CTTileSize uniform.
// Small tiles make the "every lane is saturated" early-out fire sooner, which is the whole
// point of this renderer, at the cost of binning every primitive into more tiles.
#define CT_TILE_SIZE_MIN 4
#define CT_TILE_SIZE_MAX 32

// Upper bound on how many primitives one *segment* of a tile's list can hold. The raster sorts
// one segment at a time in groupshared memory, so this is what sizes the LDS footprint, and the
// tile as a whole can hold this times the segment count. Keeping it small is worth real
// occupancy: the group's scratch array is the only thing standing between the kernel and more
// concurrent tiles.
#define CT_MAX_SEG_PRIMS 256

// The scatter hands out slots atomically, so a tile's list comes back as an arbitrary
// permutation of a range that was already in depth order. Splitting the triangle range into
// segments and giving each segment its own sub-slice of the tile keeps the order *between*
// segments for free, so the raster only sorts the segment it is currently walking - and with
// the early-out it almost never gets past the first one. The count is a renderer feature
// setting; this is the ceiling the buffers are sized for. 1 reproduces the old behaviour.
#define CT_MAX_SEGMENTS 8

// Which segment a triangle belongs to. Triangle index order is depth order, so an even split
// of the index range is an even split of the depth order.
uint CTSegmentOf(uint triIndex, uint triCount, uint segmentCount)
{
    uint seg = (triIndex * segmentCount) / max(triCount, 1u);
    return min(seg, segmentCount - 1);
}

// Thread count of the prefix-sum kernels.
#define CT_SCAN_GROUP 256

// How many primitives are shaded between two checks of the tile wide early-out.
#define CT_EARLY_OUT_STRIDE 8

// Debug visualisations. The raster kernel writes these in place of the accumulated colour,
// with alpha 0 so the composite blend (One SrcAlpha) replaces the frame instead of blending
// over it.
#define CT_DEBUG_NONE          0
#define CT_DEBUG_OVERDRAW      1
#define CT_DEBUG_TRANSMITTANCE 2
#define CT_DEBUG_TILE_LOAD     3
#define CT_DEBUG_WALK          4

// Default for the transmittance cutoff. The actual value comes from _CTEpsilon on the
// renderer feature; this is only the fallback the shaders document themselves with.
#define CT_EPSILON_DEFAULT 0.003921568  // 1/255

// One sprite as the CPU hands it over. 80 bytes, must match CTSpriteInstance in C#.
// Everything is float4 or uint4 on purpose: float2 and float3 members carry different
// alignment rules across the backends (Metal aligns float3 to 16), and a mismatch with the
// C# stride silently reads garbage.
struct CTSpriteInstance
{
    float4 center;   // xyz = world centre, w = roll in radians (billboards only)
    float4 axisX;    // xyz = world half extent along local X (oriented), w = half width (billboard)
    float4 axisY;    // xyz = world half extent along local Y (oriented), w = half height (billboard)
    float4 uvRect;   // xy = uv offset, zw = uv size
    uint4  payload;  // x = packed RGBA8 tint, y = slice, z = flags, w unused
};

#define CT_FLAG_BILLBOARD 1u

// A screen space triangle produced by the setup kernel. Two per sprite. 80 bytes.
struct CTTri
{
    float4 p01;      // p0.xy, p1.xy in screen pixels, y up
    float4 p2uv0;    // p2.xy, uv0.xy
    float4 uv12;     // uv1.xy, uv2.xy
    float4 zLod;     // device depth at each vertex, w = mip level
    uint4  payload;  // x = packed RGBA8 tint, y = slice, z = 0 when culled during setup
};

float2 CTP0(CTTri t) { return t.p01.xy; }
float2 CTP1(CTTri t) { return t.p01.zw; }
float2 CTP2(CTTri t) { return t.p2uv0.xy; }
float2 CTUV0(CTTri t) { return t.p2uv0.zw; }
float2 CTUV1(CTTri t) { return t.uv12.xy; }
float2 CTUV2(CTTri t) { return t.uv12.zw; }

float4 CTUnpackColor(uint c)
{
    return float4(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF, (c >> 24) & 0xFF) * (1.0 / 255.0);
}

// Black to blue to green to yellow to red, for the debug views.
float3 CTHeat(float t)
{
    t = saturate(t) * 4.0;
    float3 c = lerp(float3(1.0, 1.0, 0.0), float3(1.0, 0.0, 0.0), t - 3.0);
    if (t < 1.0)
        c = lerp(float3(0.0, 0.0, 0.25), float3(0.0, 0.0, 1.0), t);
    else if (t < 2.0)
        c = lerp(float3(0.0, 0.0, 1.0), float3(0.0, 1.0, 0.0), t - 1.0);
    else if (t < 3.0)
        c = lerp(float3(0.0, 1.0, 0.0), float3(1.0, 1.0, 0.0), t - 2.0);
    return c;
}

// Conservative screen space bounds of a triangle, in pixels.
void CTTriBounds(CTTri t, out float2 lo, out float2 hi)
{
    float2 p0 = CTP0(t), p1 = CTP1(t), p2 = CTP2(t);
    lo = min(p0, min(p1, p2));
    hi = max(p0, max(p1, p2));
}

#endif
