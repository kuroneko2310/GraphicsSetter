# Integrated Asset Pipeline Roadmap — GraphicsSetter

## Role

GraphicsSetter is the sole runtime texture construction and rendering-policy layer in the GraphicsSetter + MissileGirl pipeline.

It owns:

- the only Harmony patch on `ModContentLoader<Texture2D>.LoadTexture`
- DDS, PNG, and cache-blob decoding
- `Texture2D` creation and GPU upload
- texture format, color space, mip chain, maximum resolution, filter, anisotropic level, and mip bias policy
- runtime texture-memory estimates and VRAM-budget planning

MissileGirl provides optional cache metadata and immutable blob storage. It must not add a competing texture-loader patch.

## Phase 1 — Safety

Tracked by draft PR #2.

- Respect DDS and mip-bias settings.
- Return to RimWorld's loader when custom loading fails.
- Validate DDS/DX10 headers and payload sizes.
- Calculate memory per mip level and compression block.
- Avoid per-frame global texture scans in settings UI.

## Phase 2 — Cache service contract

Define an optional service boundary such as `ITextureBlobCache`:

- Request by immutable `TextureCacheKey`.
- Return validated metadata and raw data via stream, memory mapping, or equivalent zero-copy-friendly access.
- Treat service absence, exceptions, schema mismatch, and corrupt blobs as ordinary cache misses.
- Keep the service contract independent from GraphicsSetter's UI.

A texture cache key should include:

- source content hash
- decoder schema version
- target texture format
- target width and height
- mip policy and mip count
- compression quality
- linear or sRGB mode
- platform capability profile

It must not include runtime-only `mipMapBias`, `filterMode`, or `anisoLevel`.

## Phase 3 — GPU-ready texture blobs

Cold path:

1. Read PNG/DDS source.
2. Decode and validate.
3. Resize if policy requires it.
4. Generate or remove mip levels.
5. Compress to the target format.
6. Persist the GPU-ready raw mip chain through MissileGirl's blob service.
7. Upload to the GPU.

Warm path:

1. Resolve and validate a blob.
2. Memory-map or stream its exact byte range.
3. Create the matching `Texture2D`.
4. Call `LoadRawTextureData`.
5. Call `Apply(false, true)`.

Before upload, validate format, dimensions, mip count, payload length, checksum, schema version, color space, and platform support.

## Phase 4 — Texture policy

Add category-aware policy for:

- UI, fonts, portraits: preserve source quality by default
- pawn, equipment, and buildings: optional one-level reduction
- terrain, backgrounds, and decoration: optional one- or two-level reduction
- masks: prefer BC4/Alpha8 and lower resolutions where safe
- normal maps: prefer BC5 and linear sampling
- tiny icons and atlas-bound UI: optionally omit mipmaps

DDS/blob sources should support dropping top mip levels without decoding discarded levels.

Preferred target formats:

- opaque color: BC1/DXT1
- color with alpha: BC3/DXT5 or BC7
- grayscale masks: BC4 or Alpha8
- normal maps: BC5, linear
- high-quality UI: RGBA32 or BC7 according to artifact and memory trade-offs

## Phase 5 — VRAM budget

Profiles:

- Auto
- Low
- Balanced
- Quality
- Custom MB

Auto should reserve memory for Vanilla textures, UI, render textures, and a safety margin before allocating a mod-texture budget. Shared-memory GPUs need a manual override.

Build the texture plan before loading. Do not unexpectedly destroy already-visible textures to enforce a budget.

## Phase 6 — Profiling and UI

Report:

- texture format
- mipmapped/non-mipmapped
- readable/non-readable
- size bucket
- owning mod
- atlas category
- source path type: PNG, DDS, or blob
- cache hit/miss
- projected saving from dropping one top mip
- projected saving from removing mips
- compression candidates
- startup peak RAM and upload/decode timings

Estimate atlas memory from metadata. Do not create readable atlas copies merely to measure memory.

## Acceptance criteria

- Cache/service failure never prevents RimWorld's normal texture loading.
- Bias, filter mode, and anisotropic level changes never invalidate disk pixel blobs.
- Golden fixtures cover BC1, BC3, BC4, BC5, BC7, linear/sRGB, full mip chains, truncation, impossible dimensions, arrays, cubemaps, and volumes.
- Runtime tests compare cold startup, warm startup, RAM peak, estimated VRAM, hit rate, and visual output.
- GraphicsSetter remains the only patch owner for `ModContentLoader<Texture2D>.LoadTexture`.
