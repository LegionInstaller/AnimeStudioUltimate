# Performance

Notes on the speed work for Zenless Zone Zero: loading, exporting, and converting animations for FBX. All numbers come from real ZZZ data (read only). Each "before" is a build of the older code, run back and forth against the new build in the same session. Times are medians with the default workstation GC, on a 16-thread machine with 31 GB RAM.

## Numbers

| What | Data | Before | After | Speedup |
|---|---|---|---|---|
| Load | 610 blocks, 3.40 GB | 37.1 s | 11.1 s | 3.3x |
| Load | 122 blocks, 690 MB | 6.85 s | 1.58 s | 4.3x |
| Export (CLI) | 6 character blocks, 45 MB in, 1,623 files out | 42.12 s | 23.5 s | 1.8x |
| Export (CLI) | 25 mixed blocks, 141 MB in, 2,971 files out | 62.37 s | 22.1 s | 2.8x |
| FBX animation conversion | 10 heaviest clips of one character | 62.5 s | 1.2 s | |
| FBX animation conversion | 20 clips | 122.0 s | 2.8 s | 44x |

A few more details on the big load: file loading (decrypting blocks, decompressing, headers) went from 18.50 s to 2.07 s, object parsing from 10.84 s to 3.18 s, and post-processing (components, SeparateMesh, avatars) from 2.18 s to 0.40 s. Total allocations dropped from 66.36 GB to 43.47 GB. Peak working set went from 22.3 GB to somewhere between 14.2 and 20.8 GB. The live heap stayed the same (about 14 GB). Object counts, file counts, warnings and errors are identical.

The two export sets behave very differently. On character blocks, 77.6% of export time is AnimationClip YAML. On mixed blocks, 87.1% is PNG encoding for textures and sprites. So a change that only helps one of them only helps half the use cases.

Setting `DOTNET_gcServer=1` brings the character export down further, to 15.2 s (2.8x). See the GC notes below for why that isn't the default.

## What changed

### Parallel loading

Input files are independent. Each opens its own stream and decompresses into its own buffers. They are now read concurrently, then merged in input order, so `assetsFileList`, the index cache and the "first one wins" rule for duplicate CAB names come out exactly like the serial loop. Parsing objects (`ReadAssets`) also runs per serialized file in parallel, because each file has its own reader and object lists, and object constructors never reach outside their own file. Objects inside one file are still read in order.

Both loops take one item at a time. Block files range from under a kilobyte to over a hundred megabytes, and the default partitioner grows its chunks, so one thread could end up alone with a late chunk full of heavy files.

Map-building runs (`AfterBundleLoaded` set) stay serial because they release bundles one by one.

### Zero-copy CAB streams

`MhyFile.ReadFiles` used to copy every CAB out of the decompressed block buffer into its own `MemoryStream`, which added up to about 2.5 GB of copies per load. A CAB is one contiguous slice, so a view over the buffer is enough (`BundleFile` already did this). Peak RAM went down because the views replace the copies.

### Removing dynamic dispatch

The loader used `dynamic` to reach bundle containers. That pulls the C# runtime binder into the load path, and finalizing its generated dynamic methods took 8% of one measured export run. The five container types share the same two members anyway, so they now implement `IBundleContainer`. The block stream was `dynamic` for no reason at all.

### Smaller load fixes

`EndianBinaryReader.Remaining` was read on every byte of `ReadStringToNull`. Block files are opened with `FileShare.ReadWrite`, so the runtime can't cache the length and every call became a syscall (9.54% of CPU). The length is now cached for streams that can't be written to.

Lists are now sized from the element count that was just read from the file (growing them was 9.3% of load CPU). `ReadArray` no longer builds a `List<T>` and then calls `ToArray()`. A broken count is capped to the bytes left in the stream, so it can't trigger a huge allocation.

`Logger.Verbose` is now an interpolated string handler, so when verbose logging is off the string is never built. There is a call like that for every parsed object.

The `Avatar_`/`_Model` name check ran for every GameObject with culture-aware `StartsWith`/`EndsWith` (5.8% of load CPU). It's ordinal now.

Vertex data is copied once per vertex instead of once per component. Big-endian swapping no longer allocates a helper array or runs LINQ `Reverse()` per component.

`ResourceReader` searched the whole data folder (about 9,800 block files) with `Directory.GetFiles` again for every asset whose resource file was missing. Missing files are now remembered as missing.

`FindBinding` recomputed a prefix sum over the whole binding list for every curve index. The curve index to binding table is now built once per clip.

### GUI message pump

`StatusStripUpdate` and `SetProgressBarValue` called `BeginInvoke` and then waited on the handle, a synchronous hop to the UI thread. Every log line goes through `StatusStripUpdate`, so during parallel loading all workers queued up behind the message loop. Now at most one post is pending and it always draws the newest text. The flag is cleared before the text is read, so a writer racing the draw triggers a new post instead of losing its message. Progress is published with compare-and-swap so the bar can't jump backwards. In a test with 16 threads sending 400 messages each (6,400 total), the time went from 3,177 ms to 0 ms, and the last message still arrives.

### YAML nodes

Animation YAML builds one sequence per curve, with dozens to thousands of keyframes. Those child lists are now sized up front. That only saved about 4% of AnimationClip export time, because the final array still has to be allocated.

The bigger win: field names like `x`, `y`, `time`, `value`, `inSlope` repeat tens of millions of times, and each one was its own node. These key nodes can't be changed after they're built, so one shared instance per name is enough, even across threads (limited by length and count, since a mapping key can be arbitrary data). Character export went from 32.08 s to 25.70 s and peak RAM from 9,657 MB to 6,223 MB. That memory drop matters because concurrent export had pushed the peak from about 6.3 GB to 9.7 GB, with every thread building its own node tree.

The YAML emitter also formats floats with `TryFormat` into a stack buffer instead of making a new string per number. The output text and format provider are the same.

### MonoBehaviour patterns

Four regexes were compiled again for each of 5,800 MonoBehaviours. They're only used when `scrapeMonos` is on, which it isn't by default. A profile of MonoBehaviour export alone put 45% of its time in building those regexes. They're built once now.

### CLI concurrent export

Export was using 1.01 of 16 cores, and the hot work (PNG encoding, texture decoding, YAML building, float formatting) is pure per-asset compute. An export only depends on order when a second asset would land on the same directory and base name, because then `TryExportFile` starts looking for a free `name (n)`. Those cases are counted up front. Everything with a unique name gets the same path no matter which thread writes it, so it runs concurrently. The rest runs serially afterwards, in list order, like before. No file name is ever decided by a race.

Assets are grouped by serialized file, because all objects in a file share one reader position, and groups are handed out one at a time. Character blocks went from 39.55 s to 32.08 s with this step alone. The GUI export is still serial.

### FindTrack and FixBonePath

With a full moveset loaded, one character's FBX export with animations took hours. `ReadCurveData` runs once per curve per frame. One of those clips has 457 tracks, so that's millions of calls per clip, and two lookups inside were linear:

- `ImportedKeyframedAnimation.FindTrack` did `TrackList.Find(...)` with string compares over every track added so far. It now uses two dictionaries that answer the same way as before. Without an attribute, the first track ever added under that path wins, blend shape or not. With an attribute, only a track created for that channel matches, which is why a blend shape gets its own track.
- `ModelConverter.FixBonePath` called `RootFrame.FindFrameByPath`, which walks the whole frame tree, for the same path keyframe after keyframe. The result is now cached per path. The tree is fixed before animations are converted.

The amount of work depends on what's loaded, not on the character itself. 54 blocks with 685 clips came to 134,850,000 curves x frames. 7 blocks with 30 clips came to 8,293,000. If you only load the model blocks, export takes seconds. Add the animation blocks and it takes minutes.

About the animation options: "Export animations" is only checked when the FBX is written. Turning it off skips writing, but the animator's clips are still gathered and converted. "Collect animations" decides whether the animator's clips are gathered at all, but only when no clip list is passed in. "Export Animator with AnimationClip" always passes a list (the clips selected in the asset list), so neither checkbox matters there.

## Concurrency bugs that parallelism exposed

These couldn't be hit on ZZZ data before, but they were real:

- `resourceFileReaders`: `ResourceReader` opens a resource stream lazily and adds it to this dictionary, which is a multi-threaded write once `ReadAssets` runs in parallel. It's a `ConcurrentDictionary` now, and whoever loses the race closes its duplicate stream.
- `UnityCN.DecryptKey` used one process-wide `ICryptoTransform`, which isn't thread-safe. It runs under a lock now. That happens twice per bundle header, so it costs nothing.
- `ResourceReader` now seeks and reads under a lock on the reader. A `.resS` stream is shared by every asset that points into it, across serialized files, so grouping by file doesn't protect it. Decoding and encoding happen outside the lock.
- `assetsFileIndexCache` is a `ConcurrentDictionary` and `PPtr` uses `TryAdd`. PPtr resolution fills it lazily from whatever thread is exporting (material JSON export does this for every referenced object).

The ACL decoder didn't need changes. Its allocation counter is `std::atomic`, error buffers are `thread_local`, and the rest lives on the stack.

## Correctness bug: culture

The CLI never set a culture. On a system with comma decimals it wrote numbers like `x: -0,62396765` into exported files. In a YAML flow mapping the comma separates entries, so those files don't parse as vectors. This hit all 952 `.anim` and all 43 `.obj` files in the character set. JSON from Newtonsoft was fine, and the GUI was never affected because it forces `en-US` on its export task. The bug was already in the older code.

Fixed in three places: the anim export's `StringWriter` gets `InvariantCulture`, the OBJ export's `AppendFormat` calls do too, and the CLI sets `DefaultThreadCurrentCulture` at startup so threads created later are covered.

This also meant an earlier "byte-identical" export comparison was two equally broken outputs compared with each other. The reference outputs were generated again after the fix.

## Left alone on purpose

Some exports stay serial. FBX export (Animator, GameObject) passes the target folder through the process-wide working directory. `D3DDisassemble` for shaders is documented as not thread-safe. AudioClip builds and tears down a full FMOD system per clip. MonoBehaviour can pull in the Mono.Cecil loader, which caches metadata lazily.

The GC default stays workstation. Server GC made loading much faster when memory held out (5.1 s in one measurement), but on a 31 GB machine the heap also grew to 25-26 GB, it started swapping, and that run took 14.0 s against a steady 13.2 s for workstation GC. `System.GC.ConserveMemory` made things worse over twelve runs (4 outliers instead of 1). For export, server GC is a clear win (23.5 s -> 15.2 s at about the same peak), but loading and exporting happen in the same process. So `DOTNET_gcServer=1` is opt-in for machines with more memory and export-heavy runs.

There's still some variance. About 1 in 12 loads of the 690 MB set grows the heap to 16.2 GB instead of 2.71 GB and takes 4.1 s instead of 1.58 s. GC counts, total allocations and pause times are the same in both cases, and the extra is heap fragmentation. It doesn't happen in the single-threaded baseline, and it's still faster than the old 6.85 s.

Not done yet: a streaming YAML emitter without the intermediate tree, caching `FindTOS`/`FindRoots` (which still sort every loaded object for each exported clip), and a faster PNG encoder.

## How output was checked

- Load fingerprint: counts per type, SeparateMesh and avatar rows, mesh checksum, ACL checksum, vertices, zero weights, blend shape channels. Identical to the baseline, identical with `MaxParallelism = 1`, and identical across two parallel runs.
- Facial end-to-end: 6,138 blend shape values over 198 channels through the real `ModelConverter`, byte-identical CSV.
- Export trees: 1,573 non-FBX files in the character set byte-identical over 5 parallel runs, and 2,967 in the mixed set. Same file names, no empty files, no comma decimals left.
- FBX files are not byte-deterministic even between two runs of the same build: the FBX SDK derives object IDs from heap addresses, plus there's a creation timestamp and file ID. So the 50 FBX files are compared after replacing object IDs with stable indices and dropping those header fields. Geometry, normals, UVs, weights and curve values then match byte for byte.
- Model graph: skeleton, meshes, skin weights, clips, keyframes and shape keys with values match. That fingerprint was first checked against itself (two runs, 15,471 identical lines).
- Animation conversion: a digest over all converted animation data (20 clips, 9,177 tracks, every rotation and translation keyframe, 137 MB of text) is the same before and after the FindTrack/FixBonePath change. The mesh digest of a normal export is unchanged too.
