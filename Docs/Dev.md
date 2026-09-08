The goal is to create custom compute based rasteriser specifically for alpha blended transparency. Use unity render graph.
The main goal of this approach is to achieve is to minimize overdraw cost for very high amount of almost opaque transparent sprites. Especially on mobile devices.
How it works:
1. Sort geometry by depth front-to-back
2. Rasterize triangles with alpha blend by special formula:
`DstColor = DstColor + T * SrcAlpha * SrcColor`
`T = T * (1 - SrcAlpha)`
3. If `T < epsilon` stop

Investigate and write plan if work is too much to be done in one step. Use subagents to keep context small.
Log work and decisions in this document.
---

# Implementation Plan

*(written 2026-09-06)*

## Investigation summary

Starting point: empty Unity 6000.3.3f1 project on the **Built-in** render pipeline. No SRP
packages installed, `Assets/` contains only `SampleScene` and the default input actions.
Render Graph lives in `com.unity.render-pipelines.core` and is only usable from an SRP, so
URP has to be installed first.

## Approach

A tile-based (binned) compute rasterizer. Geometry is fed in as triangles (sprite quads are
split into two triangles), pre-sorted front-to-back, binned into screen-space tiles, then
each tile is shaded by one thread group that walks its triangle list in depth order and
accumulates:

```
DstColor = DstColor + T * SrcAlpha * SrcColor
T        = T * (1 - SrcAlpha)
```

and stops as soon as `T < epsilon`. Because a thread group owns a whole tile, the loop can
break for the entire tile once *every* lane is saturated, which is where the overdraw saving
comes from: with near-opaque sprites the tile terminates after a handful of layers instead
of blending every layer.

Key decisions:

- **Tile size 8x8, one thread per pixel (64 threads).** Small tiles make the "all lanes
  saturated" early-out fire much earlier than 16x16 would. Tile size is a shader define so it
  can be tuned per platform.
- **Triangles, not sprites, as the rasterizer primitive.** Matches the formula in the brief
  and keeps the door open for non-quad geometry; the sprite path just emits 2 triangles.
- **Texture2DArray atlas** rather than bindless textures. Bindless is not available on the
  mobile targets this is aimed at, so each triangle carries a slice index.
- **CPU-side depth sort first (Burst jobs), GPU sort later.** Sorting is not the interesting
  part of the problem; a GPU radix/bitonic sort is a drop-in replacement in phase 6.
- **Per-tile list ordering.** Binning scatters with atomics, which destroys depth order, so
  each tile list stores the global (already depth-sorted) primitive index and is sorted in
  LDS before shading. Since the key *is* the sorted index, a cheap LDS sort is enough.

## Phases

| # | Phase | Output |
|---|-------|--------|
| 1 | URP + Render Graph scaffolding | URP installed, pipeline asset, renderer feature that runs an empty compute pass |
| 2 | Geometry pipeline | `CTSprite` component, registry, primitive struct, CPU depth sort, GPU buffers |
| 3 | Binning | count / prefix-sum / scatter compute passes producing per-tile primitive lists |
| 4 | Raster | the actual front-to-back compute rasterizer with the transmittance early-out |
| 5 | Composite + depth | composite over the opaque scene, test against the camera depth buffer |
| 6 | Validation | stress-test scene, correctness comparison vs. normal transparent sprites, profiling |

Phases 1-5 are the working renderer; 6 is what proves the premise.

## Work log

### 2026-09-06 — phases 1-5 implemented, one open bug

**Done**

- URP 17.3.0 installed; `Assets/Settings/CT_URPAsset.asset` + `CT_UniversalRenderer.asset`
  created and assigned in Graphics/Quality settings. Depth texture is on.
- `Assets/ComputeTransparency/` holds the whole renderer:
  - `Runtime/ComputeTransparencyRenderFeature.cs` — feature + settings (atlas, pool size,
    depth test, epsilon, render pass event, shader references).
  - `Runtime/ComputeTransparencyPass.cs` — records seven render graph passes: setup, bin
    clear, bin count, three scan passes, scatter, raster, composite.
  - `Runtime/CTSpriteRegistry.cs`, `ComputeTransparentSprite.cs`, `CTSpriteData.cs`,
    `CTAtlas.cs`, `CTSpriteField.cs` (stress-test spawner).
  - `Shaders/CTCommon.hlsl`, `CTSetup.compute`, `CTBin.compute`, `CTRaster.compute`,
    `CTComposite.shader`.
- Sample scene: `CT Sprite Field` (4000 sprites), `CT Opaque Wall` for the depth test.

**Decisions forced by the hardware and the toolchain**

- *All GPU structs are built from `float4`/`uint4` only.* The first version used `float3`
  and `float2` members; Metal aligns `float3` to 16 bytes, so the C# stride and the shader
  layout disagreed and every read after the first member was garbage. `CTSpriteData` is
  4×`float4` (64 B) and `CTTri` is 4×`float4` + 1×`uint4` (80 B), with the `uint` payloads
  carried as raw bits in `w` components.
- *No group sync may sit inside control flow the compiler cannot prove uniform.* Three
  rewrites came out of this: no early `return` before the barriers, every loop that
  surrounds a barrier has a group-uniform trip count (the tile walk is chunked by a
  compile-time constant instead of being bounded by the primitive count), and the
  early-out flag is read into a local *followed by another sync* before the `break`.
- *The atlas is bound with `ComputeShader.SetTexture`, not through the render graph.*
  Importing it as an RTHandle binds it as a plain 2D render target identifier and the
  `Texture2DArray` sampler comes back empty.
- *`GL.GetGPUProjectionMatrix(proj, renderIntoTexture: false)`.* The flip that flag applies
  exists for hardware rasterization into a render texture; this rasterizer owns its pixel
  origin, and the flipped matrix mirrored the image vertically. Verified against
  `Camera.WorldToScreenPoint`: both axes now agree to within a pixel.
- *Epsilon is a renderer feature setting* (`_CTEpsilon`), not a shader constant, so the
  quality/cost trade-off is tunable per project.

**Verification method**

The scene view capture tool returns stale frames, so correctness is checked numerically:
render `Camera.main` into an `ARGBFloat` render texture, `ReadPixels`, and compare against
the front-to-back formula evaluated on the CPU. Do not trust the screenshots alone.

**Bugs found and fixed after that**

1. *Mirrored projection.* `GL.GetGPUProjectionMatrix(proj, renderIntoTexture: true)` applies
   the flip hardware rasterization needs when drawing into a render texture. This kernel
   owns its own pixel origin, so the flip mirrored the image vertically. Passing `false`
   fixes it; verified against `Camera.WorldToScreenPoint`, which now agrees on both axes to
   within a pixel.

2. *Pixels on the shared diagonal blended twice, or not at all.* A quad is split into
   (p0,p1,p2) and (p0,p2,p3), and its centre lies exactly on the shared p0-p2 diagonal.
   Evaluating the edge function from each triangle's own leading vertex rounds the two
   results independently, so such a pixel could come out positive for both halves (blended
   twice: one sprite at alpha 0.5 measured 0.75) or negative for both (whole sprites
   vanishing from the middle of a stack — with four stacked sprites only triangles
   {0,1,2,3,7} of eight ever blended). A top-left fill rule alone does not fix this,
   because the two edge functions are never exactly zero at the same time.
   The fix is `EdgeFunction` in `CTRaster.compute`: order the two endpoints canonically
   (lexicographically) before the cross product, so both triangles evaluate the *same*
   expression, and negate the result for the triangle that walks the edge backwards. The
   negation is exact, so the two values are exact opposites and the fill rule can then break
   the tie deterministically.

3. *The sample sprite textures were not what they looked like.* The generator used
   `Mathf.SmoothStep(1f, 0.75f, r)`, which interpolates between the *values* 1 and 0.75
   rather than thresholding, so the "disc" was a square of near-uniform alpha. That is what
   made every sprite render as a flat square. The generator now builds a real edge ramp.
   The rasterizer was never at fault here.

4. *The verification capture double-encoded gamma*, which washed everything out to white and
   sent me chasing a blending bug that did not exist. Captures now render into a linear
   `ARGBFloat` target and apply `LinearToGammaSpace` once, by hand.

**Verification**

Against the front-to-back formula evaluated on the CPU, for n stacked sprites at alpha
128/255 sampled at the quad centre:

| n | measured | expected | delta |
|---|----------|----------|-------|
| 1 | 0.5044 | 0.5044 | 0.0000 |
| 2 | 0.7532 | 0.7532 | 0.0000 |
| 3 | 0.8771 | 0.8771 | 0.0000 |
| 4 | 0.9383 | 0.9388 | -0.0005 |
| 5 | 0.9694 | 0.9695 | -0.0001 |

The residual at n≥4 is the half-float precision of the `R16G16B16A16_SFloat` accumulation
target, not a blending error.

**Early-out cost, first measurement**

1920x1080, 4000 sprites at alpha 0.9, whole-frame wall time in the editor (so this includes
the CPU depth sort, binning and editor overhead, not just the rasterizer):

| epsilon | ms/frame |
|---------|----------|
| 0 (no early-out) | 13.91 |
| 1/255 | 12.34 |
| 0.02 | 12.28 |
| 0.05 | 11.67 |
| 0.15 | 12.62 |

About 11% off the frame at the exact cutoff, and the curve flattens after that: the raster
pass is not the whole frame here, and the noise floor is roughly the spread between the last
three rows. A per-pass GPU timing harness is needed before drawing conclusions about the
rasterizer itself — that is the first thing to do in phase 6.

**Still to do**

- Per-pass GPU timers, and a like-for-like comparison against Unity's own transparent queue
  drawing the same sprites. That is the measurement the whole premise rests on and it has
  not been made yet.
- Run it on an actual mobile device; every measurement so far is desktop Metal.
- The CPU depth sort is `Array.Sort` on the main thread. Move it to a Burst job, then to a
  GPU radix sort, once the rasterizer stops being the bottleneck.
- Tile list pool sizing (`averagePrimitivesPerTile`, default 64) is a fixed budget. A tile
  that overflows drops its farthest primitives silently; there is no counter reporting it.
- Near-plane clipping is not implemented: a quad with any vertex behind the eye is dropped
  whole.
- A tile whose primitive count exceeds `CT_MAX_TILE_PRIMS` (1024) drops the overflow.

---

### 2026-09-06 (later) — side by side stress scene, and what it measured

**The scene**

`CTStressScene` generates one list of sprites (position, half size, atlas slice, tint) from a
seeded RNG and hands the *same* list to both rendering paths:

- **Compute Group** — one `ComputeTransparentSprite` per item, drawn by the compute rasterizer.
- **Traditional Group** — `CTReferenceSpriteBatch`, which billboards the same quads into a
  single mesh, orders the triangles back to front on the CPU and draws them through URP's
  transparent queue with `Blend SrcAlpha OneMinusSrcAlpha`
  (`Shaders/CTReferenceSprite.shader`).

`mode` switches between Compute, Traditional and Both; `sideBySideOffset` pulls the groups
apart when both are shown, and is zero by default so flipping the mode is a straight A/B over
identical pixels.

The baseline is deliberately given every advantage except the early-out itself: one draw call
instead of 4000, no per-object culling or sorting, blending done by the ROP, the same
`Texture2DArray`, and tints quantised to `Color32` so neither side gets extra precision. A
renderer-per-sprite baseline would have measured draw call overhead instead of overdraw.

**Agreement between the two paths**

4000 sprites, alpha 0.9, 960x540: mean absolute difference **0.00687**, worst pixel
**0.047**, and **no pixel** differs by more than 0.1. The residual is the different mip
selection (an analytic per-triangle jacobian against hardware derivatives) plus the half-float
accumulation target and the epsilon cutoff.

**What the timings actually showed**

First run, 1920x1080, 4000 sprites: compute **14.3 ms**, traditional **1.0 ms**. The compute
path was 14x *slower*, and the gap did not move with alpha at all — which already said the
early-out was not what was being measured.

Two experiments located it:

- Scaling sprite screen size by 8x down changed the compute cost not at all (12.0 → 12.4 ms).
  So neither binning (which scales with tiles covered) nor rasterization (which scales with
  pixels covered) dominated.
- Cost tracked sprite *count* almost exactly linearly: 1.15 ms at 1 sprite, 3.53 at 1000,
  13.2 at 4000.

Timing `CTSpriteRegistry.BuildSorted` on its own confirmed it: **9.06 ms of the ~12 ms frame
was the CPU gather**, not the GPU. Each sprite cost four separate managed-to-native transform
reads per frame (`position`, `right`, `up`, and `lossyScale`, which walks the parent chain),
and `Register` guarded with `List.Contains`, making scene construction quadratic.

**Fix and result**

`GetData` now takes the transform's `localToWorldMatrix`, read once by the registry, and
derives position and both axes from its columns; the registry builds each sprite's GPU record
in the same pass and only permutes the records afterwards, so no sprite is touched twice.
`Register` no longer scans the list.

| | before | after |
|---|--------|-------|
| `BuildSorted`, 4000 sprites | 9.06 ms | **1.78 ms** |
| compute frame, 1920x1080 | 13.20 ms | **3.51 ms** |
| traditional frame | 0.69 ms | 0.69 ms |

Image agreement is unchanged after the refactor (same 0.00687 mean).

**Where that leaves the premise**

The compute path is still about 5x the cost of the hardware path on this machine, and roughly
half of what remains is the CPU gather (1.78 of 3.51 ms). Subtracting the gather and the
editor's own ~0.7 ms per render leaves on the order of **1 ms of GPU work** against a
traditional path whose entire frame is 0.69 ms.

Two things this does *not* yet establish:

- Desktop Metal on an Apple GPU has enormous fill rate and is exactly the wrong platform to
  show an overdraw win. The whole premise is about mobile, and nothing here has run on a
  phone.
- The frame timings still bundle CPU, GPU and editor overhead together. Per-pass GPU timers
  are needed before attributing the remaining ~1 ms to any particular stage.

So: the two paths now agree on pixels, the comparison harness exists, and the first
bottleneck found was on the CPU and has been removed. The overdraw question itself is still
open and needs per-pass GPU timing on a mobile device to answer.

---

### 2026-09-06 (later still) — instanced submission API

GameObjects are gone from the compute path. The renderer now takes sprites as instance data.

**API**

```csharp
// One block of sprites, no GameObject each. Dispose it when done.
var batch = new CTSpriteBatch(capacity, "My Sprites");

var instances = batch.Instances;                 // NativeArray<CTSpriteInstance>, write directly
for (int i = 0; i < n; i++)
    instances[i] = CTSpriteInstance.Billboard(position, size, tint, slice);
batch.Count = n;
batch.MarkChanged();                             // tells the renderer to re-upload

batch.Enabled = false;                           // skip it without disposing
batch.Dispose();
```

`CTSpriteInstance.Billboard(centre, size, tint, [uvRect], [slice], [roll])` and
`CTSpriteInstance.Oriented(centre, halfAxisX, halfAxisY, tint, [uvRect], [slice])` cover the
two orientation modes; the flag lives in the instance so both can share a batch.

`ComputeTransparentSprite` still works and is still the convenient path. It now feeds an
internal batch that `CTSpriteRegistry` refreshes each frame — one native transform read per
component, which is exactly the cost the batch API exists to avoid.

**Why billboards are now oriented on the GPU**

Previously the CPU built each sprite's world space axes from the camera basis, so *every*
sprite had to be rewritten whenever the camera turned, however static the scene. The setup
kernel now takes `_CTCameraRight` / `_CTCameraUp` and builds the axes itself, with the roll
angle in `center.w`. Instance data no longer depends on the camera at all.

**What the renderer does per frame**

`CTInstanceCollector` gathers the batches into two buffers:

- **Instances** — the concatenation of every enabled batch, copied block-wise (`NativeArray.Copy`
  per batch, never per sprite), and re-uploaded only when some batch's version has moved.
- **Sorted indices** — instance indices in front to back order, which is what the setup kernel
  walks. Sorting *indices* rather than the instances keeps the instance upload a plain block
  copy.

The order is only recomputed when the camera position or forward axis actually changed, or the
content did. A batch written once, viewed from a camera that is not moving, costs nothing per
frame.

The `CTSpriteInstance` layout is 80 bytes: four `float4`s plus a `uint4` payload holding tint,
slice and flags. Same rule as everywhere else here — no `float2` or `float3` members, because
their alignment differs between backends.

**Unchanged**

`CTStressScene`'s compute group is now a single `CTSpriteBatch` instead of 4000 GameObjects.
Output is identical to the GameObject version, and still matches the traditional path to a mean
absolute difference of 0.00687 with no pixel differing by more than 0.1.

**Still on the CPU**

The depth sort. It is now `Array.Sort` over a flat key array with no native calls in the loop,
but it is O(n log n) on the main thread and it is the only remaining per-sprite CPU work when
the camera moves. A GPU radix sort is the next step if it shows up in a profile.

---

### 2026-09-06 (later still) — GPU radix sort, selectable

The depth sort can now run on the GPU. The CPU sort is unchanged and stays the default; the
choice is a setting on the renderer feature, `Sort Mode`: `Cpu` or `GpuRadix`.

**Algorithm** (`Shaders/CTRadixSort.compute`, driven from `CTRadixSorter` + the pass)

Least significant digit radix sort of (depth key, instance index) pairs, four passes of eight
bits, ping-ponged between two key/value buffers. Four passes is even, so the result always
lands back in the A buffers.

- `CSPrepare` — computes each instance's view depth on the GPU from the camera basis and maps
  the float onto a uint whose unsigned order matches the float order (flip the sign bit for
  positives, every bit for negatives). Entries past the real count are filled with
  `0xFFFFFFFF` so the padding sorts to the end where nothing reads it.
- `CSCount` — per block digit histogram, written to `hist[digit * blockCount + block]`. Each
  cell has exactly one writer, so the buffer never needs clearing between passes.
- `CSScanBlocks` / `CSScanBlockSums` / `CSScanAdd` — exclusive scan over the whole histogram,
  turning it into the destination base for every (digit, block).
- `CSScatter` — the part that has to be right. An LSD radix sort only works if each pass is
  stable, and handing out slots with atomics is not. So each block first sorts its own 256
  element chunk by the pass's digit using **eight one-bit splits** in groupshared memory —
  zeros keep their relative order, ones keep theirs — and then derives every element's rank
  within its digit from its position in that locally sorted chunk.

**Verification**

The sort was checked in isolation, driving the kernels directly over 5000 random float keys in
20 blocks and reading the result back: **0 out of range, 0 duplicates, 0 missing, 0
inversions** — an exact permutation in exact depth order.

End to end, rendering the 4000 sprite scene both ways gives identical frames. A controlled run
at an off axis camera position, comparing each mode against itself as well as against the
other:

| | mean | worst pixel |
|---|------|-------------|
| cpu vs cpu | 0.000000 | 0.000000 |
| gpu vs gpu | 0.000000 | 0.000000 |
| cpu vs gpu | 0.000000 | 0.000000 |

An earlier, less careful run had shown small differences at two camera angles (worst pixel
around 0.4 on a handful of pixels). Those did not reproduce under the controlled run above and
the isolated sort test rules out the sort itself, so the cause is unidentified rather than
explained away. Worth watching for.

**Fairness of the toggle**

Both paths skip the sort entirely on a frame where neither the content nor the camera changed;
`CTInstanceCollector.NeedsResort` gates both. Without that the CPU path would look better
purely by skipping frames the GPU path still worked through.

**Cost shape, not measured**

The GPU path adds 21 dispatches per resorted frame (one prepare, then four passes of five). No
timings taken — that is yours to run. What it removes is the last per sprite work on the main
thread: in `GpuRadix` mode the CPU only uploads instance data when a batch's version moved.

---

## Work log — configurable tile size and debug views

### Tile size as a setting

`CT_TILE_SIZE` was a compile time constant, and it has to be: one thread group owns one tile and
one thread owns one pixel, so the tile edge *is* the `numthreads` declaration. Making it a
uniform is not possible, and there was a second reason not to try — every loop that surrounds a
`GroupMemoryBarrierWithGroupSync` in the raster kernel needs a trip count the compiler can prove
is group uniform, which a value read from a constant buffer does not reliably give you on all
three backends.

So the kernel exists once per supported size. `CTRaster.compute` now declares `CSRaster4`,
`CSRaster8`, `CSRaster16` and `CSRaster32`, each a three line entry point that calls the shared
`RasterTile(tileSize, tileThreads, ...)`. HLSL inlines everything before it analyses control
flow, so the size arrives as a literal and all the loop bounds stay compile time constants. This
compiles clean on Metal for all four sizes, including 32 (1024 threads per group).

Everything outside the raster kernel takes the size as data: `CTBin.compute` gained a
`_CTTileSize` uniform for its tile range computation, and the pass computes `tilesX`/`tilesY`
from the setting.

`ComputeTransparencyPass` caches the four kernel ids at construction (`HasKernel` first, so a
size the platform refused to compile stays at -1) and resolves one per frame. An unavailable or
invalid size falls back to 8, and the fallback is written back into the local so binning uses the
size that is actually being rasterized. `Validate()` clamps a size that needs more threads than
`SystemInfo.maxComputeWorkGroupSize`, and normalises the 0 that an asset serialized before this
setting existed deserializes to.

**Verification.** Same scene (10000 sprites), same camera, rendered at all four sizes:

| tile | mean delta vs tile 4 | worst pixel |
|------|----------------------|-------------|
| 8 | 0.000000 | 0.000000 |
| 16 | 0.000000 | 0.000000 |
| 32 | 0.000000 | 0.000000 |

Bit identical. Tile size is a pure performance knob, not a quality one.

### Debug views

Three visualisations, selected by `debugMode` on the renderer feature, with `debugRange` setting
the count that maps to the top (red) of the heat ramp:

- **Overdraw** — blends actually performed per pixel. It stops climbing exactly where the
  early-out stopped the walk, so this is the picture of what the renderer is for.
- **Transmittance** — remaining `T` per pixel, greyscale. White is untouched, black is saturated
  and cut short by epsilon.
- **Tile Load** — primitives binning put into that pixel's tile, before the early-out gets a say.
  Magenta marks a tile that went over `CT_MAX_TILE_PRIMS` and dropped the surplus, which used to
  be silent.

No extra shader or pass. The raster kernel writes the debug colour into `_CTResult` with **alpha
0**, and the existing composite blend (`One SrcAlpha`, i.e. `dst = src + dst * a`) then replaces
the frame instead of blending over it.

**Verification.** Decoding the ramp back to numbers over the 10000 sprite scene, sweeping
epsilon:

| epsilon | blends/px | mean T | saturated px | binned/tile |
|---------|-----------|--------|--------------|-------------|
| 0 | 2.8 | 0.0008 | 99.6% | 52.3 |
| 1/255 | 2.5 | 0.0009 | 99.6% | 52.3 |
| 0.25 | 1.9 | 0.0254 | 76.5% | 52.3 |

Overdraw falls and transmittance rises as epsilon grows, which is what the early-out is supposed
to do, and tile load is invariant to epsilon, which it must be — binning happens before any of
this. Note the gap between 52.3 primitives binned per tile and 2.5 blends per pixel: that ratio
is the saving, measured rather than asserted.

### What the tile load view immediately found

Binned primitives per tile against tile size, same scene, `averagePrimitivesPerTile` 512:

| tile | binned/tile mean | peak | pixels in overflowed tiles | blends/px |
|------|------------------|------|----------------------------|-----------|
| 4 | 80.7 | 292 | 0 | 2.23 |
| 8 | 100.8 | 368 | 0 | 2.23 |
| 16 | 151.3 | 768 | 0 | 2.23 |
| 32 | 274.9 | 1022 | 2048 | 2.23 |

At tile 32 two tiles hit the `CT_MAX_TILE_PRIMS` ceiling of 1024 and dropped primitives. The
image is still bit identical to the other sizes, because the dropped primitives sit far behind
the point where transmittance ran out — but that is luck, not a guarantee, and a scene with lower
alpha would show it. Larger tiles need a proportionally larger ceiling.

### Open items

- `CT_MAX_TILE_PRIMS` is still a compile time constant (1024) and still bounds the groupshared
  list. It does not scale with the tile size, which is what the overflow above is.
- Still no per pass GPU timers, and still nothing has run on a mobile device.
- Near plane clipping is still absent.

---

## Work log — why the compute path loses at 8K

Reported: at 8K the compute path falls behind the traditional one, and an Xcode GPU capture
attributes 78% of the frame to `CSRaster16`. Investigated with a new debug counter plus a series
of A/B experiments; every timing below is wall clock in the editor with a `ReadPixels` forcing a
GPU sync each frame, interleaving the two modes so GPU clock ramp cannot bias one of them.

### New counter: walk length

`Overdraw` counts blends, but a pixel that has saturated still sits in a SIMD group that keeps
evaluating primitives for its neighbours, so the whole tile pays for its slowest pixel. The
`WalkLength` debug mode reports how far along its list a tile's group actually got. This is the
number that matters for cost, and nothing was measuring it.

### The early-out is not the problem

8K, 10000 sprites, epsilon 0.05:

| tile | total ms | binned/tile | walked/tile | blends/px |
|------|----------|-------------|-------------|-----------|
| 4 | 125.5 | 167.9 | 9.4 | 2.52 |
| 8 | 31.4 | 172.8 | 9.5 | 2.52 |
| 16 | 18.8 | 182.8 | 9.8 | 2.52 |
| 32 | 18.0 | 203.8 | 10.3 | 2.52 |

The walk stops after ~10 of ~180 binned primitives. The early-out works exactly as designed.
What costs is everything around it, and it costs per tile: quartering the tile area roughly
quadruples the frame.

### Where the per-tile cost goes

Two isolations. Setting epsilon above 1 makes no pixel blend and every tile bail after one chunk,
which leaves the list load and the bitonic sort. Commenting out the sort leaves the load.

| tile | total | no shading | sort disabled |
|------|-------|------------|---------------|
| 4 | 120.1 | 113.4 | 72.6 |
| 8 | 30.8 | 26.3 | 25.4 |
| 16 | 19.3 | 20.3 | 19.9 |

At tile 4 the sort alone is ~48 ms and the list load another ~40. The group loads and sorts 256
entries to consume 10. But at tile 16 — the size actually being used — all of this is inside the
noise, and capping `CT_MAX_TILE_PRIMS` to 16 (making preparation nearly free) moved the frame
only from 19.2 to 18.8 ms. **So the preparation is not what the Xcode capture is seeing.** It is
a real inefficiency, it explains why small tiles are catastrophic, and it is not the 8K problem.

### What the 8K problem actually is

The gap against the traditional path, interleaved, tile 16:

| resolution | traditional | compute | gap |
|------------|-------------|---------|-----|
| 3840x2160 | 4.53 | 5.03 | 0.50 |
| 7680x4320 | 14.90 | 16.84 | 1.94 |

The gap is proportional to pixel count — 4x the pixels, 3.9x the gap — not to primitive count.
It is a per pixel constant, and the compute path has one the hardware path does not: it writes a
full screen RGBA16F intermediate to main memory and then reads it back in the composite blit.
At 8K that is 265 MB written plus 265 MB read, every frame, on top of the same work. Halving it
by making the intermediate RGBA8 cut the 8K gap from 2.4 to 1.94 ms, which both confirms the
mechanism and shows it is not the whole of it; the composite blit itself is another full screen
pass the traditional path never runs.

Underneath this is the reason the comparison is unflattering on this hardware at all. Metal on
Apple silicon is a tile based deferred renderer: the traditional path's 90-odd blends per pixel
happen in on-chip tile memory and main memory sees one write per pixel. The overdraw this
renderer exists to eliminate is already nearly free there. The compute path replaces cheap
on-chip blending with expensive off-chip traffic. Every mobile GPU worth targeting is also a
tile based deferred renderer, which makes this a finding about the premise, not about the
implementation.

### Where it does win

4K, tile 16, sweeping depth complexity:

| sprites | traditional | compute | ratio |
|---------|-------------|---------|-------|
| 2500 | 2.89 | 3.97 | 1.37x slower |
| 10000 | 4.51 | 5.11 | 1.13x slower |
| 40000 | 14.24 | 8.62 | **0.61x, 1.6x faster** |
| 120000 | 42.86 | 39.51 | not valid, see below |

There is a crossover and it is where the design predicts: the compute path's cost is nearly flat
in depth complexity while the traditional path's is linear. At 40000 sprites (849 primitives
binned per tile, 8.1 walked, 1.06 blends per pixel) it wins by 1.6x, and 84% of what it still
spends is not shading.

The 120000 sprite row is **not a valid data point**: the tile load view shows 100% of tiles over
`CT_MAX_TILE_PRIMS`, so the compute path is dropping primitives and rendering a different image.
Whatever the number means, it is not a comparison.

### What to do about it, in order

1. **Stop round tripping a full screen intermediate.** This is the whole per pixel gap. Options:
   composite in the raster kernel by reading and writing the camera target directly, or keep the
   accumulation in a smaller format. Biggest win at high resolution, and the only one that
   addresses the actual complaint.
2. **Raise `CT_MAX_TILE_PRIMS` or make it scale with tile size** before trusting any dense scene
   measurement; at 120000 sprites every tile overflows today.
3. **Stop sorting the whole tile list.** Only ~10 of ~180 entries are consumed. This is what makes
   tile 4 unusable and it is pure waste at every size, even though it is not the 8K bottleneck.
   The principled fix is binning that emits per tile lists already in depth order, so the raster
   streams them lazily and never materialises the tail.

### Corrections to the two proposals above

**Proposal 1 as written is not available.** URP hard-codes `desc.enableRandomWrite = false` on the
camera target descriptor (`UniversalRenderPipelineCore.cs:1630`), and the camera colour attachment
is built from it, so a compute kernel cannot write the camera target directly. What is left
without forking URP: shrink the intermediate (RGBA16F to a 4 byte format was measured at 0.45 ms
of the 2.4 ms gap at 8K, and costs HDR range), and skip the raster write and composite read on
tiles no primitive reached (nothing in this stress scene, where sprites cover the screen; a real
win in a sparse one).

**Removing the sort is not free.** With the sort disabled the dense scene got *slower*, 8.62 to
10.95 ms. Sorted lists are ascending triangle indices, so `_CTTriangles[gsList[i]]` walks the
80 byte structs in address order; unsorted it scatters through a 6.4 MB buffer and misses cache.
Any replacement has to preserve depth order for locality, not only for correctness — which is what
ordered binning does and what simply deleting the sort does not.

**What the sort actually costs, measured cleanly.** Capping `CT_MAX_TILE_PRIMS` to 16 leaves the
sort enabled but makes the list trivial. At 40000 sprites, 4K, tile 16, with shading disabled:
8.52 ms falls to 6.05 ms. So the list load plus bitonic sort is **2.5 ms of a 10.1 ms frame, 24%**,
at the density where this renderer wins. Removing it would move the dense case from 1.65x faster
than the traditional path to roughly 2.3x. (Total frame time is unchanged in that capped run only
because a 16 entry list stops pixels saturating, so shading grows by as much as preparation
shrinks — and the image is wrong. It isolates the cost; it is not a fix.)

---

## Work log — segmented scatter

The triangles are already in depth order when they reach binning: `CSSetup` reads
`_CTSortedIndices[i]` and writes to triangle slots `i*2` and `i*2+1`, so triangle index order
*is* depth order by construction, which is why culled sprites keep their slot with `valid = 0`.
Nothing about the input is unsorted. What loses the order is `CSScatter`'s `InterlockedAdd` on
the per tile cursor: the slot a triangle gets depends on which group arrives first. The raster
then spent 55 barrier stages and 4 KB of groupshared rebuilding an order that existed two passes
earlier. The task was never to produce order, only to stop destroying it.

### How the order is kept

Each tile's slice is divided into `_CTSegmentCount` sub-slices, one per range of the triangle
index space. Slots inside a sub-slice are still handed out atomically, so a segment is still
unordered, but a triangle can only ever land in the sub-slice its depth range owns. The list is
therefore sorted between segments by construction, and the raster sorts only the segment it is
currently walking — which, with the early-out consuming about 8 primitives, is almost always the
first one.

The ordering comes from the layout, not from dispatch order, so the scatter stays a single pass.
The bin pipeline gains two small per tile passes:

- `CSTileTotals` folds the per segment counts into the tile total and each segment's offset
  within the tile. The per tile budget is spent front to back, so **a tile that overflows now
  drops its farthest primitives** instead of an arbitrary set of them.
- `CSSegBase` turns those offsets absolute once the scan has placed the tile, and aims each
  segment's scatter cursor at its own sub-slice.

`_CTTileCursor` is gone, replaced by `_CTTileSegCursor`. `_CTTileCount` now carries the raw
unclamped count for the debug views, and `_CTTileTotal` carries the kept count that feeds the
prefix sum. The raster's segment loop bound is the compile time `CT_MAX_SEGMENTS` because it
contains group syncs, but the buffers are sized by the setting, so unused segments read nothing
and fall through; a tile leaves the loop as soon as no pixel can take a contribution or nothing
is left past the current segment.

### Verification

960x540, 10000 sprites. Every segment count against 1 segment, which reproduces the old
behaviour, at tile 8 and tile 16:

| segments | mean | worst pixel |
|----------|------|-------------|
| 2 | 0.000000 | 0.000000 |
| 3 | 0.000000 | 0.000000 |
| 4 | 0.000000 | 0.000000 |
| 5 | 0.000000 | 0.000000 |
| 8 | 0.000000 | 0.000000 |

Bit identical, non powers of two included. Against the traditional path: compute luma 0.56319 vs
0.56496, mean 0.00179, worst pixel 0.04665 — the usual residual, unchanged.

### Payoff

3840x2160, tile 16, 40000 sprites, traditional interleaved with every segment count so clock
drift hits both equally:

| | ms | vs traditional |
|---|-----|----------------|
| traditional | 15.58 | — |
| 1 segment | 11.14 | 0.71x |
| 2 segments | 8.91 | 0.57x |
| 3 segments | 8.16 | 0.52x |
| 4 segments | 8.06 | 0.52x |
| 6 segments | 7.97 | 0.51x |
| 8 segments | 8.19 | 0.53x |

**3.1 ms of an 11.1 ms frame, 28%**, and the compute path goes from 1.4x to 1.9x faster than the
traditional one. It plateaus at 4, which is the default; 6 and 8 are inside the noise and cost
more memory. This is close to the 2.5 ms the isolation predicted, and the extra comes from the
better cache behaviour of shorter, denser lists.

At 8K with 10000 sprites it changes nothing, exactly as predicted: that case was never bound by
list preparation, and capping `CT_MAX_TILE_PRIMS` to 16 had already shown preparation was worth
only 0.7 ms there.

### One bug worth recording

Sizing the segment buffers by the setting rather than by `CT_MAX_SEGMENTS` meant the raster had
to read `_CTSegmentCount` to compute the stride — but that uniform was only being set in
`SetBinConstants`, which the raster pass does not call. It read 0, every segment was skipped and
the compute path rendered almost nothing (luma 0.029 against 0.565). The first run after the
change looked like the *traditional* group had gone missing, because the harness compared against
it; it was the compute side that was blank. Worth remembering that a missing compute uniform
reads as zero and fails silently rather than loudly.

### Still open

- `CT_MAX_TILE_PRIMS` is still 1024 and `gsList` is still sized for it, so the raster still burns
  4 KB of groupshared per group even though a segment now holds a quarter of that. Shrinking it
  is the next easy occupancy win, and it is now safe to do because overflow drops the farthest
  primitives rather than arbitrary ones.
- The per pixel gap at high resolution is untouched; that is the intermediate buffer round trip,
  and URP blocks the direct fix.

---

## Work log — smaller groupshared list and an 8 bit intermediate

### Segment sized groupshared

`CT_MAX_TILE_PRIMS` (1024) is gone, replaced by `CT_MAX_SEG_PRIMS` (256). The raster only ever
sorts one segment at a time, so the tile total was never what sized the scratch array; `gsList`
drops from 4 KB to 1 KB per group and `chunkCount` from 128 to 32. Each segment now gets its own
budget in `CSTileTotals` rather than the tile spending one shared budget front to back.

**A tile's capacity is now 256 x the segment count.** At the default 4 segments that is 1024,
exactly the old cap, so the default behaviour is unchanged — but the setting now controls
capacity as well as speed, which the tooltip says and which the tile load view shows in magenta.

Measured, 960x540, against the traditional path (the pre-change figures were mean 0.001793 /
worst 0.046653 at 10000 sprites and 0.001854 / 0.037239 at 40000):

| sprites | segments | mean | worst |
|---------|----------|------|-------|
| 10000 | 2, 4, 8 | 0.001939 | 0.046971 |
| 40000 | 4 | 0.002004 | 0.038335 |
| 40000 | 8 | 0.002003 | 0.038335 |
| 40000 | **2** | **0.033349** | **0.823523** |

The 2 segment row at 40000 sprites is the new capacity limit doing exactly what it says: 849
primitives binned per tile over 2 segments is ~425 each against a ceiling of 256, so the surplus
is dropped and the image is visibly wrong. Four segments and up are clean. This is the trap the
setting now carries, and it is why the default stays at 4.

**The occupancy win did not materialise.** 4K, 40000 sprites, tile 16, interleaved: 8.46 ms at 4
segments against 8.06 ms before the change, and 7.79 ms at 6 segments — all inside the run to run
spread (the traditional path moved 15.58 to 15.74 across the same runs). Quartering the
groupshared footprint changed nothing measurable on this GPU. The change stands on the memory
argument and on mobile, where threadgroup memory budgets are tighter, not on a measured speedup
here. Recorded as a negative result rather than quietly dropped.

### 8 bit intermediate without HDR

`_CTResult` is `R8G8B8A8_UNorm` when `cameraData.isHdrEnabled` is false, `R16G16B16A16_SFloat`
otherwise. The accumulated colour is a sum of `T * alpha * colour` whose total weight cannot
exceed 1, so LDR content stays inside [0, 1] and does not need the float target; the buffer is
written by the raster kernel and read straight back by the composite, which at 8K is 530 MB of
pure per pixel traffic that the hardware path never pays.

Cost in accuracy: the mean error against the traditional path rises by 0.00015 (0.001793 to
0.001939 at 10000 sprites, where no capacity limit is in play so the delta is the quantisation
alone). Transmittance is quantised to 8 bits, which is the same resolution as the default
epsilon, so the early-out is unaffected. Deep gradients in near black may band; switching the
URP asset to HDR restores the float target.

Note the format is chosen per camera from the HDR flag, not from a setting. This project's URP
asset has `supportsHDR` off, so the 8 bit path is the one running.

The earlier controlled measurement of this change on its own put it at 0.45 ms of the 2.4 ms per
pixel gap at 8K. The runs above could not resolve it again: wall clock at 8K in the editor swings
about 20% between blocks, and the traditional path alone moved 20.64 to 17.66 ms between runs.
The 8K figures in this session are not precise enough to claim a number, only the direction.

## Work log — a demo scene, and the traditional path rebuilt on RenderMeshInstanced

Two things at once: a scene meant for a build rather than for the profiler, and a rewrite of the
baseline the compute path is measured against.

### The baseline no longer bakes a mesh

`CTReferenceSpriteBatch` rewrote four vertices per sprite into one mesh every frame, because
billboards follow the camera. That is a per frame mesh upload the baseline was paying for reasons
that have nothing to do with what is being compared, and it flattered the compute path. It is gone,
replaced by `CTInstancedSpriteBatch`: one `Graphics.RenderMeshInstanced` over a shared unit quad,
the quad turned towards the camera in the vertex shader exactly as `CTSetup.compute` does it, and
the only per frame CPU work is the depth sort the transparent queue genuinely requires.

The sort is a Burst `IJobParallelFor` producing a packed `ulong` key (monotonic float bits in the
high word, index in the low word), a `SortJob` over it, and a gather into a separate draw ordered
copy. Keeping the sorted copy separate means `Instances` stays index aligned with whatever filled
it, which is what lets the demo update it incrementally.

Instanced primitive order is guaranteed by the graphics APIs, so a back to front instance array
blends correctly in one draw.

### Three faults, stacked, all presenting as "the draw is invisible"

The rewritten shader rendered nothing, at any instance count. It took three separate fixes, each
of which fully hid the next.

**One: an instanced property that is not a material property.** `_BaseColor` and `_SliceParams`
were declared with `UNITY_DEFINE_INSTANCED_PROP` but not in the `Properties` block. They then read
back as zero, and zero alpha through `Blend SrcAlpha OneMinusSrcAlpha` writes nothing at all - the
frame is bit for bit identical to one with no draw in it. Declaring them made a plain
`MeshRenderer` with the same material draw, which is what separated "the shader is broken" from
"instancing is broken".

**Two: no `#pragma target`.** `UnityInstancing.hlsl` gates its support on `SHADER_TARGET`, and the
default is well below the bar. The instanced variant still compiles - every
`UNITY_ACCESS_INSTANCED_PROP` just silently resolves to the material's own value. `#pragma require
2darray` does not raise `SHADER_TARGET`; only `#pragma target` does. Now 4.5.

**Three: the per instance property block never arrives.** With both of those fixed the draw
renders, the transforms are per instance - the cloud has the right shape and grows with the count -
and every sprite is still the material's white. No exception, no warning, no error: Unity accepts
the 96 byte instance struct, uploads the matrix, and drops the rest.

Rather than keep reverse engineering that, the tint and the atlas slice now ride in the transform,
which is the one piece of per instance data that is delivered reliably. A translate plus a per axis
scale uses six of the matrix's sixteen elements, so `_m01`, `_m02`, `_m10`, `_m12` carry RGBA and
`_m20` carries the slice; `CTInstancedSpriteBatch.Instance.Create` packs them and the vertex shader
reads them straight back out. The instance struct is now nothing but a `float4x4`.

The price is that the matrix is no longer a well formed transform. Nothing here needs it to be: the
vertex shader reads elements out one at a time and never multiplies by it, the sprites are unlit so
the derived `unity_WorldToObject` is never sampled, and the only visible effect is on culling, where
each instance's bounds grow by at most a unit against a cloud of radius 11.

### The measurement harness lied twice before any of that

Worth recording, because both failures produce plausible looking numbers.

An offscreen probe camera in **edit mode** never sees an immediate mode draw. `RenderMeshInstanced`
followed by `cam.Render()` renders nothing - including for a URP/Lit control that renders perfectly
in play mode. An earlier control run in this same harness reported a healthy luma and was taken as
proof that the API was fine; it was measuring the scene behind the draw, not the draw.

And a probe camera does not isolate anything from the compute pass, which collects its sprites
itself and ignores the camera's culling mask. A probe with `cullingMask = 0` and a black clear still
came back at luma 0.718 - all of it the compute path's own cloud. Every A/B from here on runs in
play mode and captures a baseline frame with no draw submitted, as the control.

### Numbers

Play mode, 480x270, `ARGBFloat`, one camera position, compute against the instanced baseline:

| count | mean | worst | lumaCompute | lumaTraditional |
|-------|------|-------|-------------|-----------------|
| 100 | 0.000287 | 0.887966 | 0.02005 | 0.01997 |
| 1000 | 0.002589 | 0.380586 | 0.07461 | 0.07598 |
| 5000 | 0.004325 | 0.243431 | 0.08697 | 0.08968 |
| 10000 | 0.004775 | 0.765218 | 0.08618 | 0.08898 |

In line with the RGBA8 quantisation floor the two paths have always agreed to. The worst case
column is edge pixels: two rasterizers disagreeing about coverage on a sprite silhouette, which is
expected and does not accumulate.

### The demo scene

`Assets/Scenes/CTDemo.unity`, first in the build settings. A hollow ball of sprites of radius 11
around an opaque sphere, so the depth test has something to prove; a camera that orbits, tilts and
dollies between 14 and 40 units on three periods that do not divide into each other; the UI panel;
and Graphy.

`CTDemoCloud` allocates once for the largest step on the ladder and never resizes. Every sprite is
a pure function of its index and the seed - position, size, hue and slice all come out of an integer
hash of the index - so growing the cloud from 1000 to 10000 generates and builds only the indices
`[1000, 10000)`, and shrinking it only lowers a count. Nothing is cleared and nothing is rebuilt.
The alpha slider is the one control that does touch every sprite, because that is what it means,
but the items never stored an alpha, so it rebuilds the two instance arrays and not the cloud. Both
instance formats - the compute path's `CTSpriteInstance` and the hardware path's matrix - are
written in the same Burst job from the same quantised colour, so switching paths costs nothing at
the moment of the switch and the two are provably built from the same numbers.

`startPath` on the cloud picks which renderer the scene opens on. It exists because entering play
mode reconstructs everything, so there was otherwise no way to open straight into the hardware
baseline, which made it awkward to test.

### The panel

`CTDemoUI.uxml` and `CTDemoUI.uss` under `Demo/UI`, assigned to the scene's `UIDocument`; the
component only looks controls up by name and wires them to the cloud. Instance count with `-` and
`+` over the ladder, an alpha slider, a renderer dropdown, a debug view dropdown and Reset. The
dropdown choices are filled from the enums in code rather than spelled out in the UXML, so there is
one place to change them.

The debug views come out of the compute pass, so the dropdown greys out while the traditional path
is running. The panel reaches the pass through a direct serialized reference to the
`ComputeTransparencyRenderFeature` sub asset - the feature reads `m_Settings` live every frame, so
writing to it takes effect immediately without touching `SetActive` and dirtying the renderer asset.

Verified in play mode by layout rather than by screenshot: the panel resolves to 276x294 at (16, 16)
on a 1791x1235 screen, every named control has a non degenerate `worldBound`, and driving them moves
the model - a `-` click takes the count 10000 to 5000 and the label with it, the slider writes
`cloud.Alpha`, the dropdown switches the path.

### Overdraw for the hardware baseline

The Overdraw view existed only for the compute rasterizer, which counts its blends inside the loop
that performs them. The hardware path has no such loop to instrument, so its count is a second draw
of the same billboards into a one channel target with additive blending, resolved through the same
`CTHeat` ramp and the same `debugRange`. Both views now count the same thing - sprites covering a
pixel that pass the depth test - so the difference between the two pictures is exactly what the
early-out saves.

The count is drawn by the renderer feature (`CTTraditionalOverdrawPass`), not by the batch. An
immediate mode `Graphics.RenderMeshInstanced` can only render into the camera's own colour target,
and accumulating a count in there would mean fighting whatever format and colour encoding that
target happens to have - a linear increment of 1/255 stored through an sRGB encode quantises badly
at the dark end, and an HDR target changes the arithmetic again. The pass allocates its own
`R32_SFloat`, so the sum is exact and cannot saturate.

The draw is procedural rather than instanced through a mesh. `CTInstancedSpriteBatch.Instance` is
now a bare `float4x4`, so the batch mirrors the instances into a `GraphicsBuffer` and the pass calls
`DrawProcedural` with six vertices and one instance per sprite. That sidesteps
`CommandBuffer.DrawMeshInstanced`'s 1023 instance limit, which at the top of the demo's ladder would
otherwise have been a thousand draw calls. The buffer is re-uploaded only when the batch's version
moves, so a static cloud uploads once. The instance buffer travels in a `MaterialPropertyBlock`
rather than on the material: material state is resolved when the command buffer executes, so
several batches sharing one material would all end up drawing the last one's instances.

The count pass borrows the camera's depth attachment read only, so a sprite behind the opaque object
does not count - which is what the compute path's own depth test does before it increments.
Sorting is skipped entirely while counting: an additive sum does not care about order.

The batch draws either the image or the count, never both, and the feature leaves the pass out of
the frame unless some live batch is asking for it, so nothing is paid when the view is off.

Verified in play mode at 1000 sprites, `debugRange` 64:

| alpha | mean diff | heat, compute | heat, traditional |
|-------|-----------|---------------|-------------------|
| 0.05 | 0.00113 | 0.02132 | 0.02126 |
| 0.90 | 0.01956 | 0.01998 | 0.02126 |

Three things fall out of that. At alpha 0.05 the two views agree to 0.001: transmittance barely
drops, the early-out almost never fires, and the compute rasterizer really is walking every sprite -
which is the control that says both views are counting the same thing. At alpha 0.90 they diverge
and the compute view is the cooler of the two, which is the early-out doing its job. And the
hardware count is identical at both alphas, to five decimals, because the hardware path shades every
layer no matter how opaque it is - the one number in this table that is a property of the renderer
rather than of the scene.

In the demo the Debug dropdown now stays enabled on the traditional path. Overdraw is the one view
both renderers can produce; Transmittance, Tile load and Walk length describe the compute
rasterizer's tiles and its early-out, so they do nothing while the hardware path is showing.

## Work log — the first Android build

Three failures at once on a Galaxy S23 (Vulkan): the frame rate pinned at 30, the compute path
drawing untextured sprites, and the traditional path drawing nothing at all. Two of them have
identified causes and fixes; the third is instrumented rather than guessed at.

### The frame rate was the platform's, not the renderer's

A player that never asks for a frame rate gets the platform's answer, and on Android that is 30.
On top of that the quality level the demo ships on has vSync on, and Optimized Frame Pacing
(Swappy) was enabled in the player settings, which paces the player to divisors of the display
refresh. All three had to go for the number on screen to describe the renderer:
`CTDemoBootstrap` sets `QualitySettings.vSyncCount = 0` and `Application.targetFrameRate` (300 by
default, exposed on the component), and `optimizedFramePacing` is off in the player settings.

### The traditional path had no instanced variant to run

The default shader stripping setting is Strip Unused for instancing variants, and "unused" means
"no material asset in the build has GPU Instancing ticked". Every material in this project was
created at runtime from a `Shader` reference, which the stripper cannot see, so `INSTANCING_ON`
did not exist on the device. `Graphics.RenderMeshInstanced` then had nothing to run and the whole
batch drew nothing, while the editor - which compiles variants on demand - looked perfect.

The fix is the ordinary one: ship `CTReferenceSprite.mat` as an asset with instancing enabled and
copy it at runtime. `CTInstancedSpriteBatch.SetMaterial` now takes a `Material` rather than a
`Shader`, and `CTDemoCloud` and `CTStressScene` reference the material.

Second fix in the same area: `#pragma target 4.5` is more than this shader needs and drops it
entirely on any device that falls back to GLES3.0, which reports shader level 35. The instancing
gate in `UnityInstancing.hlsl` and `Texture2DArray` both sit at 3.5, so that is what it asks for
now. The Android graphics API list is Vulkan then GLES3, so this only bites on the fallback - but
it bites silently, which is the worst kind. Re-measured in the editor after the change: compute
against traditional, mean 0.000439 / 0.002345 / 0.004236 / 0.004476 at 100 / 1000 / 5000 / 10000,
unchanged from 4.5.

### The untextured compute sprites are not diagnosed

No device here to attach to, and the atlas is packed at load time from the project's textures, so
the failure could be the pack, the binding, or the sampling. Rather than guess, the packing now
says what it did and the demo shows the facts on screen.

`CTAtlas.Build` no longer calls `Apply` after `Graphics.CopyTexture`. The copy is GPU side; the
array's CPU side is still the zeroes it was allocated with, and uploading those over a completed
copy is at best a waste and at worst the whole bug. `Apply` now runs only on the path that
actually writes CPU data. That path is new: `Graphics.CopyTexture` from a `Texture2D` to a
`Texture2DArray` is a copy between texture types, which not every device supports, so where
`copyTextureSupport` lacks `DifferentTypes` the raw bytes are uploaded instead - and where the
source is also not readable, the log says exactly which of the two is missing rather than leaving a
black atlas on a device nobody can attach a debugger to. A slice that fails the size and format
match, and an atlas that packs nothing at all, now log as well.

The panel carries a device report - graphics API and shader level, screen size, frame rate cap and
vSync, compute and instancing support, `copyTextureSupport`, the atlas's actual dimensions, format
and mip count, and whether the sprite shader is supported and instanced. Every line in it picks
between causes that are otherwise indistinguishable on a phone.

The next measurement is on the device, and it is one comparison: with the traditional path now
drawing, does it show textures? Both paths sample the same `Texture2DArray`. If both are
untextured the atlas is the problem and the report says which failure it hit; if only the compute
path is, the fault is in how the compute pass binds the array, which it does on the ComputeShader
asset rather than through the command buffer because importing the array as an RTHandle binds it
as a plain 2D target and the array sampler comes back empty.

## Work log — per pass GPU timers

The first item on the list from the investigation above: every number in this document so far is
whole frame wall clock, which bundles the CPU gather, the GPU work and the editor's overhead into
one figure and cannot say which of the eleven dispatches the frame went into. The only genuine per
pass number ever recorded here is the 78% on `CSRaster16` from a one-off Xcode capture.

### What was added

`CTGpuTimers` is a registry of `CustomSampler`s created with `collectGpuData: true`. Every dispatch
in `ComputeTransparencyPass` now goes through a `Dispatch` helper that brackets it in
`BeginSample`/`EndSample`, and the composite blit is bracketed the same way. Seventeen timers:
setup, the six radix sort passes, the eight bin passes, raster, composite.

Three details worth recording:

- **The sample brackets the dispatch alone**, not the parameter binding above it, so the number is
  the hardware's time in the kernel rather than the cost of recording the pass.
- **The four radix passes share one sampler each**, so a sort row reports the total across all four
  rather than a quarter of it. That is the number the question needs.
- **`gpuSampleBlockCount`, not the nanoseconds, is the validity signal.** A pass that did not run
  this frame and a platform with no GPU timing both report zero nanoseconds; without the block
  count an idle pass would drag its own average towards zero. This is what makes the sort rows go
  blank rather than reading 0.000 in `Cpu` mode.

Off by default (`gpuTimers` on the renderer feature). Timestamp queries are not free and on a tile
based GPU can split work that would otherwise batch, so this finds the expensive pass; it does not
quote the renderer's cost.

### In the demo panel

The panel now has two pages behind tabs under the title. Seventeen rows will not share a panel with
the controls - the first attempt put them in a 210px scroll area at the bottom and the rows were
effectively invisible - so Timers is a page of its own: the `GPU timers` toggle, the total, and the
per pass list in dispatch order grouped by stage. Values redraw at 5 Hz over an exponential moving
average, and only while that page is showing; the readings are a few frames late by nature, because
the CPU runs ahead of the GPU.

Two layout faults on the way there, both of which only a measurement catches:

- **A fixed `height: 15px` on the row, with `overflow: hidden` on the name.** At `font-size: 11px`
  the line box in this theme is taller than that, so every glyph in the list was cut off top and
  bottom. Rows size to their text now.
- **The theme's own Label margin**, which at seventeen rows added enough to push the last three
  under the scroll. Zeroed on the two row labels.

Verified by layout in play mode rather than by eye, which is what found both: every label in the
panel measured with `MeasureTextSize` at the width it actually got, against its `contentRect`.
**39 labels, 0 clipped**; 17 rows, all 17 fully in view, content 273.5px inside a 420px cap so
nothing scrolls; Controls page 420px, Timers page 511px.

A note on testing this: the panel caches its controls in `OnEnable`, so reimporting the UXML or USS
while play mode is running leaves `CTDemoUI` writing into a detached visual tree - rows appear to
vanish and the list reads empty. That is the test setup, not the panel. Restart play mode after
touching the layout files.

### The result on this machine: nothing, and it is not the wiring

**Metal in the editor reports no GPU timings at all.** Verified in play mode on the demo scene:

| | |
|---|---|
| samplers created | 17 of 17, all valid |
| CPU side | `CT Raster` cpuBlocks 4, `CT Sort Count` cpuBlocks 16 (4 passes x 4 cameras) |
| GPU side | `gpuSampleBlockCount` 0, `gpuElapsedNanoseconds` 0, every pass |

So the samplers really are in the command buffer and really are being entered. The control that
settles it: Unity's own `Camera.Render` marker, with its recorder explicitly enabled, reports
`cpuBlocks=2` and `gpuBlocks=0` in the same session. `ProfilerDriver.profileGPU = true` changes
nothing. This is the backend, not this code.

The panel says so rather than showing a column of dashes: after a three second grace period
`CTGpuTimers.Silent` goes true, the note names the graphics API and points at the external tool,
and a warning goes to the console once.

### What this means for the plan

The measurement the whole plan was waiting on is still not available on the development machine.
Where the timers can be expected to work is Vulkan on the Android device, which is the platform the
premise is about anyway - but that is untested, and the honest state is "wired, verified as far as
the platform allows, unverified where it counts".

So the order stands, with the first step now concrete:

1. Run the demo as a development build on the S23 with the timers on. Either the rows fill in, and
   the per pass breakdown finally exists, or they do not and every future measurement here has to
   come from Snapdragon Profiler or AGI.
2. Part A of the setup hoist (winding, degenerate reject, `invArea` out of the per pixel loop).
   Bit-identical, verifiable with the existing harness, does not depend on the timers.
3. Edge based tile reject in `CTBin` plus the edge coefficient form, together. Both want the same
   `CTTri` change, and it grows the struct 80 to 112 bytes, which is exactly the trade the timers
   were supposed to arbitrate.

## Work log — a switch for the atlas sample

`textures` on the renderer feature, and a `Textures` toggle on the demo panel's Controls page.
Off, neither renderer samples the atlas and both shade from the tint alone.

**Both paths, from one place.** The compute rasterizer is driven by a uniform on the raster kernel,
the hardware baseline by a global shader float, and both come from the same setting, pushed once per
camera in `AddRenderPasses`. A switch that reached only one of them would turn every A/B in this
document into a comparison of the switch. The compute shader needs its own `SetComputeIntParam`
because a `ComputeShader` does not read globals set with `Shader.SetGlobalFloat`.

**The sense is inverted in the shaders** — `_CTUntextured`, not `_CTTextured`. An unset global reads
0, and 0 has to mean ordinary shading, otherwise a scene with this feature disabled, or the frames
before it first runs, would render untextured with nothing to explain it.

**What it is not.** Not a clean isolation of what sampling costs. Without the texture's alpha every
sprite is as opaque as its tint, so transmittance falls faster and the early-out fires sooner - the
compute path changes both its shading cost *and* its walk length at once. Read it next to the Walk
Length view, which shows the second effect directly. The tooltip says so.

The branch in the raster kernel is on a uniform, so no lane diverges and the sample is simply not
issued; the barycentrics are still computed because the depth test needs them.

### Verification

Play mode, 10000 sprites, camera frozen:

| | luma | coverage |
|---|------|----------|
| compute, textures on | 0.48918 | 0.8312 |
| compute, textures off | 0.52046 | 0.8462 |
| traditional, textures on | 0.58663 | 0.9538 |
| traditional, textures off | 0.59921 | 0.9623 |

Both paths respond, and in the same direction: brighter and covering more, which is what losing the
disc's alpha ramp does. The two blocks are not comparable to each other - the compute rows come from
a render texture and the traditional rows from a screen capture that includes the UI panel - because
of the trap already recorded in this document: `cam.Render()` into a probe target does not see an
immediate mode `RenderMeshInstanced` draw, so the hardware path measured 0.003 luma that way. Its
numbers come from `ScreenCapture.CaptureScreenshotAsTexture`, which reads the real frame.

The compute path moves more than the baseline does, which is the early-out shifting as well as the
shading - exactly the confound noted above, visible in the numbers.

## Work log — trivial reject and trivial fill

Item 3 from the investigation list, and the answer to the third question it opened with: no, this
renderer had no trivial reject and no trivial fill. Binning took a triangle's screen space bounding
box and listed the triangle in every tile the box touched. That is a bad filter here for a specific
reason: a sprite is one quad split into two triangles along its diagonal, so **both** halves carry
the whole quad's bounds, and every tile the quad touches got both of them. Roughly half of every
tile list was primitives that cannot produce a pixel in that tile, and the raster paid for them
twice - once in the bitonic sort, which is superlinear in the segment length, and again in the walk.

### What was added

`CTBuildTileEdges` and `CTClassifyTile` in `CTCommon.hlsl`, used by `CSCount` and `CSScatter` in
`CTBin.compute`, and a `covered` flag consumed by `CTRaster.compute`. One classify per (triangle,
tile) pair returns outside, partial or covered:

- **outside** - the triangle never enters that tile's list, so it costs nothing downstream.
- **covered** - the triangle covers the whole tile, and the raster skips the per pixel coverage test.

Counting and scattering have to agree tile for tile or the counts and the writes disagree, so both
run the identical test from the identical setup function.

The setup winds the triangle the same way the raster winds it, and builds the three edge functions
in the same order, so "inside" means the same thing on both sides. Per tile the cost is three
multiply-adds and two comparisons against precomputed corner offsets: a tile is axis aligned, so
which of its four corners maximises an edge is decided per axis by the sign of that axis'
coefficient, and both extreme corners are a fixed offset from the tile origin, hoisted out of the
loop by the setup.

### The three decisions worth recording

**The box tested is the whole tile, not the box of pixel centres.** The tile is half a pixel larger
on every side than the pixels the raster actually samples, and that slack is what makes the test
safe in floating point without an epsilon anywhere. This expression and the raster's per pixel one
round differently - they are different expressions, compiled into different kernels - but the
disagreement is on the order of a thousandth of a pixel against half a pixel of geometric margin.
Reject uses the box's maximum, so it only fires when the whole tile plus its margin is outside;
accept uses the box's minimum, which is stricter than accepting over pixel centres would be. Both
errors are therefore in the direction that keeps pixels rather than loses them. A tuned epsilon
would have had to be scaled by the edge coefficients and the screen size, and would still have been
a guess; the geometry gives the margin for free.

**The coverage flag rides in bit 0 of the list entry, not the top bit.** The raster recovers depth
order by bitonic sorting the tile list on the raw value, so a flag above the index would sort every
flagged entry away from the position its index earned - the sprites would come back out of order.
Below the index it only breaks ties, and a triangle appears at most once in a tile, so there are
none. `CTPackEntry` / `CTEntryTri` / `CTEntryCovered` keep that in one place. The `0xFFFFFFFF` pad
the sort uses is unaffected: it still sorts last, and the walk never reads past the real count.

**Degenerate triangles are dropped at bin time, under a tighter bound than the raster's.** The
raster skips a triangle whose area is 1e-6 or below; binning drops one below 1e-7. Making the bin
bound stricter keeps what binning discards a strict subset of what the raster would have discarded
anyway, so a one ulp disagreement between the two kernels cannot cost a sprite.

### What trivial fill does and does not save

It removes the three `EdgeAccept` calls and the divergent `continue`. It does **not** remove the
three edge function evaluations, because those values are the barycentrics - the uv interpolation
and the depth test need them whether or not the pixel could have failed. Keeping them means the
shaded result is bit-identical to the old path, which is what made the verification below possible.

The further step is available and deliberately not taken yet: for a covered tile the canonical
endpoint ordering inside `EdgeFunction` is pointless, because no pixel sits on an edge and the fill
rule has nothing to decide, so a plain `Cross2` would do at roughly half the cost. That changes the
last bits of the barycentrics and so the uv, which forfeits the bit-identical check. It belongs with
the edge coefficient form of `CTTri` (`CTTri` 80 to 112 bytes), where the whole per pixel setup
collapses to three multiply-adds and the check has to be a tolerance comparison rather than an
equality.

### Verified: the image does not change

`tileReject` on the renderer feature switches the whole classify off, restoring the old bounding box
binning, so the two can be captured back to back in one play session. 10000 sprites, 1847x1195,
orbit camera frozen, textures on, compute path:

| | scene pixels differing | max channel delta |
|---|---|---|
| tile 16, reject on vs off | 0 | 0 |
| tile 16, control off vs off | 0 | 0 |
| tile 8, reject on vs off | 0 | 0 |
| tile 8, control off vs off | 0 | 0 |

Bit-identical over 2.2 million pixels, at two tile sizes, with a same-settings control proving the
comparison can see a difference at all.

A note on the method, because the first run of it lied: the demo panel redraws its own numbers every
frame, so **any** two captures differ inside it. The first mask was cut from the bounding box of the
control diff and was too small - it missed the panel's bottom edge, and the tile 8 comparison came
back with nine differing pixels that were the panel's own text. The control had eight of them at the
same coordinates, which is what identified them. Mask the whole panel, not the part that happened to
change in one pair.

### Measured: what it does to the tile lists

Tile Load view, 50000 sprites, tile 16, four segments, `debugRange` 512, as a share of the screen
outside the panel. The view's colours are read back by class rather than by inverting the ramp, so
these are buckets, not means:

| primitives binned into the tile | reject off | reject on |
|---|---|---|
| 128 - 256 | 0.0% | 7.2% |
| 256 - 384 | 0.2% | 89.3% |
| 384 - 512 | 6.1% | 3.4% |
| 512 and over | 31.0% | 0.0% |
| **overflowed and dropped primitives** | **62.7%** | **0.1%** |

The distribution moves down by about a factor of two, which is exactly what the two-triangles-share-
the-quad's-bounds argument predicts, and the magenta collapses. That second row is not a performance
result, it is a correctness one: at 50000 sprites this scene was silently dropping primitives over
five eighths of the screen, and now it is not. The 256 per segment cap had not changed - the lists
simply stopped being padded with primitives that could not draw.

### Measured: frame time

Whole frame wall clock, editor, Metal, vSync off and the 300 fps cap removed, each sample averaged
over 8500 to 12800 frames with the camera frozen:

| | reject off | reject on | repeat, off |
|---|---|---|---|
| 10000 sprites, tile 16 | 4.677 ms | 4.475 ms | 4.682 ms |
| 50000 sprites, tile 16 | 6.690 ms | 6.455 ms | - |

4.3% and 3.5%. The repeat puts the run to run noise at 0.005 ms, so a 0.20 ms difference is real,
but read the size of it with two things in mind.

The first is that this is whole frame wall clock, the instrument this document has complained about
throughout: it bundles the CPU gather, the CPU depth sort (this ran in `Cpu` sort mode), the editor's
own overhead and every one of the eleven dispatches into one number, and the binning and raster
passes are only part of it. The per pass timers that would apportion it are wired and still silent
on Metal.

The second is that at 50000 the comparison is unfair to the new path in the off direction: reject off
was dropping primitives over most of the screen, so it was doing *less* raster work and producing a
wrong image, and it was still slower. The honest single number is the 10000 sprite row, where both
configurations render the identical image.

### The switch

`tileReject` on the renderer feature, default on, no demo panel row - the inspector is enough, and
the panel is for things a person watches while the camera moves. Off restores bounding box binning
exactly, including packing the coverage flag as zero, so the entry format does not depend on the
setting.

## Work log — tile reject as a keyword instead of a uniform

`_CTTileReject` was a uniform read inside the tile loop of `CSCount` and `CSScatter`. The choice is
fixed for the whole dispatch, so it is a compile time choice wearing a runtime disguise: the branch
itself is scalar and nearly free, but keeping it forces the compiler to emit `CTBuildTileEdges` and
the classify in both configurations. `#pragma multi_compile _ CT_TILE_REJECT` and two `#ifdef`s
remove them from the variant that does not want them.

### How the keyword is set, and why not through the command buffer

`ComputeCommandBuffer` does expose `SetKeyword(ComputeShader, in LocalKeyword, bool)`, but using it
inside a render graph pass requires `builder.AllowGlobalStateModification(true)`, and the render
graph documentation is explicit about what that costs: *"This will introduce a render graph sync
point in the frame and cause all passes after this pass to never be reordered before this pass"*,
and it forces `AllowPassCulling(false)` as well. Eight sync points across the bin stage to remove a
scalar branch is a bad trade.

So the keyword is set on the shader asset while the graph is recorded, once per frame, before any
pass is added. That is what Unity's own compute shaders do - `STP.cs` sets `shaderKeywords = null`
and then `EnableKeyword` inside the `AddComputePass` scope. It is safe here because every dispatch
of `binShader` in a frame wants the same value: the setting lives on the renderer feature, not on
the camera, so two cameras cannot disagree. The day one shader needs two different values in one
frame, this pattern breaks and the sync point becomes unavoidable.

`SetKeyword` is wrapped in a guard that skips an invalid keyword. Setting a keyword a compute shader
does not declare logs an error *per call*, which at one call per frame is a console that scrolls, and
the failure mode - a shader asset older than the code - deserves a missing optimisation rather than
that.

### Verified

10000 sprites, tile 16, camera frozen, panel masked: keyword on versus off, **0 differing scene
pixels**, exactly as the uniform version measured.

That test alone cannot tell a working keyword from a stuck one, so the positive control is the Tile
Load view at `debugRange` 128, as a share of the screen:

| primitives binned into the tile | reject off | reject on |
|---|---|---|
| 0 - 32 | 0.00% | 0.11% |
| 32 - 64 | 0.21% | 34.20% |
| 64 - 96 | 7.68% | 35.44% |
| 96 - 128 | 38.40% | 0.02% |
| 128 and over | 18.51% | 0.00% |

The distribution halves, which is the same result the uniform version produced, so the keyword is
genuinely switching the compiled variant.

### The cost

`multi_compile` applies to the whole file, so all eight bin kernels are compiled twice, not just the
two that read the keyword. They are small and this is the cheapest of the keyword conversions; the
raster kernel, where the same treatment is worth much more, already has four variants for tile size
and needs the variant count watched.

## Work log — the remaining uniform branches as keywords

Three more choices that are fixed for a whole dispatch, converted from uniforms to
`#pragma multi_compile` in `CTRaster.compute`: `CT_DEBUG`, `CT_DEPTH_TEST`, `CT_UNTEXTURED`.
`_CTReversedZ` is left as a uniform on purpose - it is a platform constant guarding one select
inside the depth test path, and a fourth keyword would double the variant count to pay for it.

### Why the debug one is the point

The branches were uniform and nearly free. What they cost was everything the compiler had to keep
alive for the path not taken, and the debug counters are the worst of it: `shaded` is per pixel and
incremented in the innermost loop, so as a runtime choice it holds a register across the entire walk
in a kernel that has none to spare. `walked`, `rawCount` and `tileStart` are group uniform and
cheaper, but they are dead weight too. With `_CTDebugMode` a uniform, the final write also kept both
the shaded result and the whole `DebugColor` computation live to the end of the kernel.

The result is measurable and was measurable before any of the timing work: **`CSRaster32` no longer
emits X4714**. That warning - "sum of temp registers and indexable temp registers times 1024 threads
exceeds the recommended total 16384" - had been in this project's console since the beginning, and
it is a statement about occupancy, not style. It is gone with the debug variant compiled as well as
without, on tile 32, which is the heaviest kernel the project has.

### `CT_DEPTH_TEST` also removes work the branch was hiding

Without the keyword the triangle's device depths are still swapped by the winding fix and `z` is
still live even when nothing reads it. Compiled out, the swap and the depth load both disappear.

### The global keyword that would not stick

The hardware baseline used `Shader.SetGlobalFloat` from `AddRenderPasses`, which worked. Replacing
it with `Shader.SetKeyword(GlobalKeyword, bool)` from the same place did **not**: the keyword reads
back off on the next frame and the baseline never switches. Confirmed twice, and the direct control
is that the same call from an editor command works immediately - so the API is fine and the problem
is calling it from inside the render loop.

The fix moves the decision to something each side owns. `CTInstancedSpriteBatch` keeps its own
material clone, so it sets the keyword on that material in `Render`, from a static the renderer
feature writes each frame. The compute rasterizer sets its own local keyword on the shader asset
while the graph records, as the bin shader already did. One setting, two owners, no global state.

A second trap on the way: `GlobalKeyword.Create` throws when it runs while a `ScriptableObject` is
still being constructed, and a renderer feature's `Create` is called from `OnEnable`, which is inside
that window. The exception propagates out of `UniversalRendererData.Create` and takes the entire
render pipeline asset down - the editor renders nothing and reports the failure as a Blitter error
further down the stack. The global keyword is gone now, but the rule is worth keeping: keyword
objects belong in `AddRenderPasses` or later, never in a feature's construction path.

### Verified

Camera frozen, 10000 sprites, panel masked. Every switch has to change what it claims to change, and
the default configuration has to change nothing:

| | result |
|---|---|
| default settings vs the image before the conversion | **0 differing scene pixels** |
| compute, textures on vs off | luma 0.60587 to 0.62520 |
| baseline, textures on vs off | luma 0.61315 to 0.62746 |
| compute, depth test on vs off, alpha 0.9 | 0 differing pixels |
| compute, depth test on vs off, alpha 0.06 | 5.85% of the scene, luma 0.58174 to 0.58687 |
| Tile Load view | still draws, keyword tracked in both directions |

The depth test row is the interesting one. At the demo's normal alpha, turning the depth test off
changes nothing at all: the cloud is a hollow shell, the opaque core sits inside the hollow, and
every pixel saturates on the near shell long before the walk reaches anything the core could have
occluded. Thin the sprites until transmittance survives to the far side and the core's silhouette
appears, 5.85% of the frame. The keyword works; at alpha 0.9 the early-out gets there first, which
is the entire premise of this renderer showing up in a test that was meant to measure something else.

Keyword state was also read back off the shader assets directly (`IsKeywordEnabled`) and tracks the
settings in both directions, which is what distinguishes a working switch from one stuck on.

### The cost

`CTRaster.compute` is now four tile sizes times eight keyword combinations: **32 compiled kernels**,
up from four. Reimport of both shader files takes 0.23s here, so the editor cost is nothing, but this
is the file to watch: a fourth keyword would make it 64. If it ever comes to that, the answer is not
another `multi_compile` but explicit `#pragma kernel` entry points, which this file already uses for
the tile size - `RasterTile` is an inlined function taking the size as a literal, and a compile time
argument for anything else would specialize it the same way while enumerating exactly the
combinations that are wanted.
