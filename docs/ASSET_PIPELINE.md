# GraphicsSetter integrated asset pipeline

GraphicsSetter is the sole owner of `ModContentLoader<Texture2D>.LoadTexture`. MissileGirl/Gagarin is an optional cache service; it does not patch the texture loader.

## Implemented load flow

1. Resolve the owning mod and any `RocketRules` texture policy through the optional Gagarin bridge.
2. Prefer a matching DDS sidecar only when DDS loading is enabled.
3. Build a cache key from source SHA-256, GraphicsSetter algorithm version, selected dimensions, mip policy, compression policy, color space, VRAM profile, and platform capability.
4. Reuse a validated GPU-ready blob through memory mapping when present.
5. Otherwise decode DDS or the original image, reduce resolution by complete mip/half-size levels, generate or omit mipmaps, compress suitable textures, and store the exact raw Texture2D payload as an immutable blob.
6. Upload the texture and apply runtime filter, anisotropic level, and mip bias.
7. Return to RimWorld's original loader if the custom path fails.

Mip bias, filter mode, and anisotropic level are deliberately excluded from disk-blob keys. They are applied live to already loaded textures.

## DDS validation

The loader validates the legacy and DX10 headers, dimensions, mip counts, resource type, array size, cubemap/volume flags, payload length, and selected mip range. It supports BC1/DXT1, BC3/DXT5, BC4, BC5, BC7, Alpha8, RGB24, RGBA32, RGB565, ARGB4444, and RGBA4444 where Unity exposes the matching runtime format. DX10 UNorm and sRGB variants retain their declared color-space behavior.

Top DDS mip levels can be skipped without decoding discarded bytes. Unsupported or malformed DDS files fall back to the source image and then to RimWorld.

## Policy and VRAM budget

Implemented profiles are Auto, Low, Balanced, Quality, and Custom. Auto reserves part of detected graphics memory for RimWorld, UI, render textures, and safety margin. Custom exposes both a MOD-texture budget and maximum dimension.

Textures are categorized as UI, portrait, icon, pawn, building, terrain, background, mask, normal, or other. Categories control maximum dimensions, mipmaps, compression, and linear sampling. Tiny textures can omit mipmaps. External per-mod/path overrides are accepted through MissileGirl `TexturePolicy` rules.

## Memory and diagnostics

The profiler calculates every mip level and BC block rather than applying a flat one-third estimate. It reports per-mod totals, format totals, mipmapped/non-mipmapped counts, readable counts, atlas estimates without creating readable atlas copies, projected savings from removing mipmaps or dropping one resolution level, cache hit rate, decode time, and tracked runtime texture count.

## Failure behavior

Cache-service absence, reflection errors, corrupt blobs, invalid headers, decoding failures, and unsupported formats are ordinary cache misses. They never prevent RimWorld's normal loader from running.
