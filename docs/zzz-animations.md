# ZZZ animations (ACL)

Most ZZZ character animation clips are ACL-compressed. This page covers how this fork decodes them, what the HoYo scalar format looks like, and what is still off.

## What was broken upstream

On upstream master, `AnimeStudio.ACL.ZZZ` vendored acl 2.0.99. Its version enum stops at `v02_01_99 = 8` and has no HoYo version, so any ZZZ clip with a version 100 blob fails `is_valid()` with "Invalid algorithm version". The native side hands back an empty clip, and the managed converter then runs off the end of the buffer. On one test block master exported 0 of 10 clips (`IndexOutOfRangeException` in `AnimationClipConverter.AddTransformCurve`).

Two more problems on master: the database streamer was always passed as null, so the ACL database was never initialized, and the `StreamingInfo` behind a ZZZ clip was never read, so the bulk data never reached the decoder.

The `feat/acl_fix` branch had a HoYo-patched acl, but the scalar decode call was commented out (every float curve came out as 0) and its `write_float1` wrote into the transform part of the frame.

Upstream's newer `AnimeStudio.ACL.ZZZV2` still has the unfinished database branch described below and skips scalar tracks. Don't copy its headers over this tree.

## What this fork uses

`AnimeStudio.ACL/AnimeStudio.ACL.ZZZ` now holds acl 2.1.0 with the HoYo patch (`compressed_tracks_version16::vHoYo = 100`, `latest = vHoYo`). The DLL keeps the old name, so nothing else had to change. Genshin still goes through `AnimeStudio.ACL.DB` (acl 2.1.99, `mhy`).

A ZZZ clip carries two separately compressed blobs in `m_ClipData`:

| Blob | Version | Track type | Decoded by |
|---|---|---|---|
| Transform | 8 (`v02_01_99`, stock) | 12 (qvvf) | stock transform context |
| Scalar | 100 (`vHoYo`) | 0 (float1f) | HoYo scalar context |

So the HoYo patch only matters for the scalar side. Layout on the managed side (`ZZZACLClip`):

- `m_TransformData`: from offset 0, size is the `uint32` at the start.
- `m_ScalarData`: starts at the transform size rounded up to 16. If nothing is left after the padding, the clip has no scalar tracks.
- `m_databaseData`: the inline `compressed_database` header.
- `m_DatabaseData`: bulk data from the clip's `StreamingInfo`. ZZZ doesn't set the negative muscle clip size marker GI uses, so the record is only read if a whole one is left in the object.

`DBACL.DecompressTracksZZZ` passes all four buffers with their sizes to the native `DecompressTracksZZZ`. The native side checks each blob's declared size against the buffer before touching it, because acl trusts the header and an over-claiming blob would read past the allocation.

Native decode, in order:

1. Build the database context. Inline bulk data is used directly. Stripped bulk data goes through two `null_database_streamer`s (medium, then low tier) and `stream_in`. If the header wants more bulk data than was provided, the database is dropped and only the frames inside the clip are used.
2. Initialize the transform and scalar contexts, with the database if the blob's header says it has one.
3. Decode transforms and scalars in two separate passes, each with its own try/catch. acl asserts are compiled as throws (`ACL_ON_ASSERT_THROW`), so a bad scalar track costs you the float curves, not the clip or the process.

Settings: all three settings structs use `version_supported() = any` (acl requires the database and decompression settings to agree, and one database context serves both blobs). The scalar settings also set `skip_initialize_safety_checks() = true`, because `database_context::contains()` casts the blob to a transform header, reads the database header offset from the wrong place, and always fails for HoYo scalar blobs.

Output is one flat float array. Each frame is `10 * T + S` floats: T transform tracks as `[quat xyzw, translation xyz, scale xyz]`, then S scalars. The scalar region is always reserved, even when the scalar pass fails, because the converters step through the buffer by `m_CurveCount`.

## HoYo database scalar tracks

`hoyo_tracks_header` is 28 bytes, seven `uint32`s, and is a union of two layouts:

| Offset | Normal scalar | Database scalar |
|---|---|---|
| 0 | `num_bits_per_frame` | `range_data_size` |
| 4 | `metadata_per_track` | `num_segments` |
| 8 | `track_constant_values` | segment headers offset |
| 12 | `track_range_values` | `unk` (sub-track types, see below) |
| 16 | `track_animated_values` | `database_header_offset` |
| 20 | | `database_constant_values` |
| 24 | | `database_range_values` |

The decoder picks the layout from whether a database was passed in, not from the header bit. ZZZ facial clips use the database layout.

Upstream left the database branch of `decompress_tracks_v0` unfinished: `animated_values` stayed null, `num_bits_per_component` kept the placeholder `num_tracks`, and the end of the track loop is an empty if/else. Decoding without the database reads the wrong union and asserts with "Invalid bit rate: 52". Decoding with it trips the 23-bit limit in `unpack_scalarf_uXX_unsafe`. The non-database branch was always fine.

The branch is now implemented in `acl/decompression/impl/decompression.hoyo.h`, for float1f only. How the database layout works:

- The offset in `unk` points to `packed_sub_track_types`: an array of `uint32`, 16 sub-tracks per entry, 2 bits each starting at the MSB. Track i is `(entries[i / 16] >> (30 - 2 * (i % 16))) & 3`.
- Code 0 (default): value is 0.0, nothing stored.
- Code 1 (constant): next float from `database_constant_values`, in track order.
- Code 2 (variable): uses the ordinal among variable tracks, not the track index. The bit rate is `format_per_track_data[ordinal]` of the segment, and the range is `(min, extent)` at `database_range_values[2 * ordinal]`. Bits come from the segment's animated data. A bit rate of 32 bits means a raw float with no range.
- Each keyframe has its own format and animated data pointer and its own bit offset, because the two keyframes can be in different segments (`seek_v0` already sets both up).
- `range_data_size` turned out to be the number of variable tracks.

Reading the sub-track types as a flat byte stream is the obvious mistake. On little endian it scrambles the order inside every group of 16, but the counts of constant and variable tracks still match, so the output looks plausible while every value sits on the wrong track.

Sample blob (a blink facial clip): 612 bytes, 47 tracks, 31 samples. The sub-track types take 3 entries (12 bytes). The 8 variable tracks have bit rates `[13,13,14,13,13,14,13,13]`, which add up to 106, the `animated_pose_bit_size` in the segment header. `sample_indices` has 23 bits set, so 23 frames are stored in the clip and 8 come from the database bulk data.

## Facial clips

ZZZ facial animation (`*_Ani_Galgame_Facial_*`) drives blend shapes on `SeparateMesh_*_Face` and `*_Eyebrow` (27 and 6 channels: `Fac_Mth_*`, `Fac_Eye_*`, `Fac_Ebr_*`). Those values live in the scalar tracks. Many of these clips also exist as classic streamed/constant clips with the same 33 channels, which gives an exact reference to compare against.

## Verified

On ZZZ PC data:

- Transforms: 890 of 890 ACL clips over 26 character blocks decoded with real curves. None threw, none came out all zero. Rotations are unit quaternions.
- Variable scalar tracks vs. the classic variant: 70 tracks over 8 clips from two characters, all match, max error 9.8e-4 (13/14 bit quantization).
- Constant and default tracks: 294 tracks over 12 clips, 288 exact. The other 6 are code 0 tracks whose reference values (0.0006 and 0.0009) are below one quantization step, so the compressor dropped them.
- Through `ModelConverter`: 198 channels, 6138 samples, all within tolerance, worst 0.0015.
- End to end: `Fac_Eye_R_Close` in the exported FBX shows a full blink peaking at 90.69, within 8e-4 of the reference on every frame.
- Regression run: 1022 of 1022 ACL clips, all 638 with scalar tracks decoded, no crashes. A rerun on one character's facial blocks: 30 of 30 scalar clips with real values.

Blender import of the shape key animation hasn't been checked. The FBX file is the last verified step.

## Known issues

- `NPC_Female_Cecilia_Ani_Galgame_Facial_Angry` (block `2190724455`) fails its scalar pass with `Invalid bit rate: 32`. It has an 80 byte database header but no bulk data. The transform pass is unaffected, the clip still produces non-zero scalar values and doesn't crash, but those values haven't been compared against the classic variant.
- The database layout was worked out on single-segment clips. Per-segment range data isn't read in that path.
- The database path only handles float1f. Other scalar types with a database fall through to the old unfinished code.
- The native side keeps the last error (`GetLastDecompressError`), but the managed side doesn't read it yet.
- FBX animation export needs the Avatar. With an optimized transform hierarchy and the Avatar in a block that isn't loaded you get "can't find Avatar to deoptimize". Upstream behaves the same.
