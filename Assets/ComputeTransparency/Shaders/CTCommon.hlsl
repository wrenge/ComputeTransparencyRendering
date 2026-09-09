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
//
// The vertices arrive already wound counter-clockwise and the triangle's setup - the reciprocal
// of twice its area and the per edge bits below - is computed once here rather than once per
// pixel per tile in the raster's innermost loop. It all fits in space the struct already had, so
// the hot loop reads the same 80 bytes it always did.
struct CTTri
{
    float4 p01;      // p0.xy, p1.xy in screen pixels, y up, wound counter-clockwise
    float4 p2uv0;    // p2.xy, uv0.xy
    float4 uv12;     // uv1.xy, uv2.xy
    float4 zLod;     // device depth at each vertex, w = mip level
    uint4  payload;  // x = packed RGBA8 tint, y = slice, z = CT_TRI_* bits, w = asuint(1/area)
};

// payload.z. Bit 0 survives from when the field was a plain valid flag; the rest is the edge
// setup the raster used to redo per pixel.
#define CT_TRI_VALID 1u
#define CT_TRI_FLIP0 2u   // edge (p1,p2) is walked against the canonical endpoint order
#define CT_TRI_FLIP1 4u   // edge (p2,p0)
#define CT_TRI_FLIP2 8u   // edge (p0,p1)
#define CT_TRI_TIE0  16u  // that edge keeps a pixel sitting exactly on it (the fill rule)
#define CT_TRI_TIE1  32u
#define CT_TRI_TIE2  64u

float2 CTP0(CTTri t) { return t.p01.xy; }
float2 CTP1(CTTri t) { return t.p01.zw; }
float2 CTP2(CTTri t) { return t.p2uv0.xy; }
float2 CTUV0(CTTri t) { return t.p2uv0.zw; }
float2 CTUV1(CTTri t) { return t.uv12.xy; }
float2 CTUV2(CTTri t) { return t.uv12.zw; }

bool  CTTriValid(CTTri t) { return (t.payload.z & CT_TRI_VALID) != 0u; }

// 1 / (2 * signed area), the barycentric divisor. Positive, because the triangle is wound.
float CTTriInvArea(CTTri t) { return asfloat(t.payload.w); }

float Cross2(float2 a, float2 b)
{
    return a.x * b.y - a.y * b.x;
}

// The two per edge decisions the raster used to make for every pixel it tested, made once in
// setup instead. Both depend only on the edge's endpoints.
//
// The first is the canonical endpoint order. The two triangles a quad is split into share its
// diagonal and walk it in opposite directions; evaluating the cross product from each triangle's
// own leading vertex rounds the two results independently, so a pixel on that diagonal could come
// out positive for both halves (blended twice) or negative for both (the sprite vanishes out of
// the middle of a stack). Ordering the endpoints the same way in both triangles makes the
// expression identical, and the single negation is exact, so the two values are exact opposites.
//
// The second is the top-left style fill rule: of the two triangles sharing an edge, exactly one
// keeps a pixel lying on it, picked by the direction the edge is walked in.
uint CTEdgeBits(float2 a, float2 b, uint flipBit, uint tieBit)
{
    float2 dir = b - a;
    uint bits = 0;
    if ((a.x > b.x) || (a.x == b.x && a.y > b.y))
        bits |= flipBit;
    if ((dir.y > 0.0) || (dir.y == 0.0 && dir.x < 0.0))
        bits |= tieBit;
    return bits;
}

// Edge function at p, evaluated from the canonical end of the edge. flip comes from CTEdgeBits.
float CTEdgeFunction(float2 a, float2 b, float2 p, bool flip)
{
    float2 lo = flip ? b : a;
    float2 hi = flip ? a : b;
    float w = Cross2(hi - lo, p - lo);
    return flip ? -w : w;
}

// tie comes from CTEdgeBits and decides only the w == 0 case.
bool CTEdgeAccept(float w, bool tie)
{
    if (w > 0.0)
        return true;
    if (w < 0.0)
        return false;
    return tie;
}

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

// A tile list entry: the triangle index with a "covers this whole tile" flag in bit 0.
// The flag has to sit below the index rather than above it. The raster recovers depth order by
// sorting the list on the raw value, so a high bit would sort every flagged entry away from the
// position its index earned; a low bit only ever breaks ties that cannot happen, because a
// triangle appears at most once in a tile.
uint CTPackEntry(uint triIndex, uint covered) { return (triIndex << 1) | covered; }
uint CTEntryTri(uint entry)                   { return entry >> 1; }
bool CTEntryCovered(uint entry)               { return (entry & 1u) != 0u; }

// Per tile classification of a triangle, the two halves of the usual trivial reject / trivial
// accept pair.
#define CT_TILE_OUTSIDE 0
#define CT_TILE_PARTIAL 1
#define CT_TILE_COVERED 2

// Edge setup for that test. Edge k is e(p) = a[k]*p.x + b[k]*p.y + c[k], positive strictly
// inside. This is the coefficient form, not the form the raster evaluates: it is cheaper per
// tile, and it is allowed to disagree at the last ulp because the box it is tested against is
// half a pixel larger than the pixel grid on every side. See CTClassifyTile.
struct CTTileEdges
{
    float3 a;
    float3 b;
    float3 c;
    float3 offMax;   // e at the tile corner furthest along the edge's inward normal
    float3 offMin;   // e at the corner furthest against it
};

// The triangle is wound counter-clockwise and non-degenerate by the time it gets here: setup
// does both, and a triangle that failed either never reaches binning at all, because
// TriTileRange drops it on CT_TRI_VALID first.
CTTileEdges CTBuildTileEdges(CTTri t, float tileSize)
{
    float2 p0 = CTP0(t), p1 = CTP1(t), p2 = CTP2(t);

    CTTileEdges e;

    // e(p) = Cross2(B - A, p - A) for the edges (p1,p2), (p2,p0), (p0,p1) - the same three the
    // raster evaluates per pixel, in the same order.
    e.a = -float3(p2.y - p1.y, p0.y - p2.y, p1.y - p0.y);
    e.b =  float3(p2.x - p1.x, p0.x - p2.x, p1.x - p0.x);
    e.c = -(e.a * float3(p1.x, p2.x, p0.x) + e.b * float3(p1.y, p2.y, p0.y));

    // A tile is axis aligned, so which of its four corners maximises an edge is decided per axis
    // by the sign of that axis' coefficient, and both extreme corners are a fixed offset from
    // the tile's origin. Hoisting them here leaves three multiply-adds per tile below.
    e.offMax = (max(e.a, 0.0) + max(e.b, 0.0)) * tileSize;
    e.offMin = (min(e.a, 0.0) + min(e.b, 0.0)) * tileSize;
    return e;
}

// Outside: no pixel in the tile can pass, so the triangle never has to enter the tile's list.
// Covered: every pixel passes, so the raster can skip the coverage test entirely.
//
// The box tested is the whole tile, which is half a pixel larger on each side than the box of
// pixel centres the raster actually samples. That slack is what makes the test safe in floating
// point: half a pixel dwarfs the rounding difference between this expression and the raster's
// per pixel one, so neither answer can be wrong in the direction that loses pixels.
uint CTClassifyTile(CTTileEdges e, uint2 tile, float tileSize)
{
    float2 org = (float2)tile * tileSize;
    float3 v = e.a * org.x + e.b * org.y + e.c;

    if (any(v + e.offMax < 0.0))
        return CT_TILE_OUTSIDE;

    return all(v + e.offMin > 0.0) ? CT_TILE_COVERED : CT_TILE_PARTIAL;
}

// Conservative screen space bounds of a triangle, in pixels.
void CTTriBounds(CTTri t, out float2 lo, out float2 hi)
{
    float2 p0 = CTP0(t), p1 = CTP1(t), p2 = CTP2(t);
    lo = min(p0, min(p1, p2));
    hi = max(p0, max(p1, p2));
}

#endif
