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
