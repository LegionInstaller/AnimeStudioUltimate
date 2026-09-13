# Loader, map, mesh and asset browser fixes

What this fork changes in loading, CAB maps, mesh linking and the asset browser, compared to upstream AnimeStudio. Nearly all of it is about Zenless Zone Zero.

## One CAB name, several files

ZZZ ships different serialized files under the same CAB name in different `.blk` files. Upstream treats a CAB name as unique and throws away any later file with a name it has already seen. Load a few hundred blocks together and whole CABs vanish, with every object in them. On Cecilia's 272-block set that was 1851 skipped CABs and 34 animation clips that exist on disk but never got loaded.

These duplicates are mostly not copies. Across 1223 blocks, 97.9% of the extra copies had different content, and cheap header checks (object count, byte size) get it wrong about two times out of three. So the fork keeps every copy.

What changed:

- A loaded file is identified by container path, bundle offset and name (`SerializedFile.UniqueKey`), not by CAB name alone.
- `assetsFileIndexCache` maps a name to every candidate instead of one.
- If a `PPtr` points at a name with several candidates, `PPtr.Pick` takes the one from the same container as the file holding the reference, otherwise the first. The candidate list is cached, the choice is not.
- `PPtr.Set` compares files by reference, not by name.
- `.resS` readers are keyed by container plus name. ZZZ names a `.resS` after its CAB, so two same-named CABs bring two same-named `.resS` files. Without this a mesh could quietly read its vertex data from the wrong container.

With one candidate, which is the normal case, nothing behaves differently. The memory cost is around 7 to 15% on character sets with lots of duplicates.

## CAB map format V3

The map had the same problem. It stored one location per CAB name, and the dependency walk finds a block's CABs through that location. If the entry belonged to another block, your block's CABs were invisible to the walk and their externals were never pulled in. Zhenzhen was the obvious case: 506 clip references, none resolvable, and a dependency closure of 49 blocks that held only 1 of the 9 blocks with her clips.

`CABMap` is now `Dictionary<string, List<Entry>>`. The file starts with `0xFF "CABMAP"` and a version number, and V3 writes a location count per name followed by the locations.

When a name has several locations, the map does not pick one. It only stores names, not who refers to them, so it can't know which copy is meant. Every location goes into the closure and the number of ambiguous names is logged at the end of the run. The real choice happens in `PPtr.Pick`, where the referring file is known.

With a map built over all 9781 blocks, Zhenzhen's closure went from 49 to 285 blocks and resolved clip references from 0 to 502. The last 4 are a gap in the data: the CAB is loaded and the path ID isn't in it.

The price: the full map is about 35% bigger (129 MB to 175 MB), building it needs about 30% more RAM and loading it takes about 27% longer. Build time and closure time barely move.

Old maps:

- V1 (no header) and V2 maps still load. Each name just gets a one-element list.
- An old map does not get the fix, because the extra locations were never written into it. Rebuild it.
- A build that only knows V2 rejects a V3 map with `NotSupportedException` instead of misreading it. Upstream has no header at all and can't read V2 or V3.

Smaller things in the same code: duplicate CAB names are logged at verbose level while building, the map is written with `File.Create` so a smaller map fully replaces a bigger old file, and the reader checks every count against the bytes left and reports a corrupt map with its offset.

## Separate meshes by container id

Many ZZZ character prefabs have renderers with no mesh reference. The loader attaches the mesh afterwards ("SeparateMesh"), and upstream found it by name. That falls apart when several meshes share a name. Remielle has eight meshes called `Remielle_Face` in different blocks, with different vertex counts, and some of them have no blend shapes. The name lookup kept the first one in load order, and in a folder load that was a copy with 0 blend-shape channels. So Remielle exported without shape keys. Load the blocks in another order and the bug went away, which is why not everyone saw it.

The link was in the data all along. The child GameObject has a `NapLodController` that stores the full asset path for each LOD level:

```
Assets/OriginalResRepos/ART/DiscreteMeshAssets/Avatar_Female_Size02_Remielle_Origin_Model/Remielle_Face.mesh
```

Upstream cut that down to `Remielle_Face`, which is exactly the part that isn't unique. The folder is what tells the copies apart.

ZZZ's AssetBundle container table doesn't hold readable paths, it holds numbers, and those numbers are the XXH64 of the lowercased asset path. Hashing the path above gives an id that matches exactly one of the eight copies, the one with 32 channels.

The fork indexes each bundle's container table (container id to mesh) and tries these in order, stopping at the first hit (`AssetsManager.SeparateMeshCandidates`):

1. container id of the NapLodController path
2. container id of a rebuilt path, `<prefix><avatar>_Model/<child>.mesh`, for `_Model` prefabs that have no NapLodController (the prefix comes from NapLodController paths in the same load)
3. file name from the NapLodController path
4. `SeparateMesh_<avatar>_<child>`
5. the plain child name

1 and 2 look in the container index, 3 to 5 in the name index. A miss on 2 costs nothing. A renderer that already has a mesh reference is never touched, even when that reference points into a block that isn't loaded. The log line `SeparateMesh attached: N via container id, N via derived container id, ...` shows which route fired how often.

Remielle now exports 44 blend-shape channels (32 on the face, 12 on the eyebrows) instead of 0, and her facial clips carry real blend-shape curves. Cecilia's export is unchanged, her 32 meshes are just found by container id now instead of by name.

There is no "take the copy with the most blend shapes" rule. It would have worked here by luck and gone wrong the first time a character uses a variant without shapes on purpose.

## Blend-shape curves follow the binding

Collecting shape keys never cared about mesh names. `ModelConverter` takes the channels of every skinned mesh under the animator that has any, and they aren't only on faces: `Lycaon_Body_3`, Brujas's weapon and the eye on her gun all have animated channels.

What did depend on names was deciding which mesh an animation curve drives. Upstream took the first morph mesh with a channel of that name and only used the renderer path from the binding as a fallback. If two meshes of one character share a channel name, every curve lands on the first mesh.

Now the binding decides. Its path hash is resolved to the renderer, and a per-renderer table (`morphChannelsByPath`, filled while collecting morphs) maps the attribute hash to the channel. The name lookup is only used when the path doesn't resolve. Legacy `m_FloatCurves` follow the same rule.

For the characters checked this changed nothing in the output, because the game keeps channel names apart between meshes (`Fac_Mth_Aa1` on the face, `Fac_Mth_Aa1_Body` on the body). With a channel renamed on purpose to collide across two meshes, the curves stay on the mesh the binding names.

## _UI vs _Model

Two unrelated upstream problems hit these two prefab families, and upstream's branches each fixed only one of them.

- `_UI` prefabs are heavy on transitions. ZZZ's `TransitionConstant` has extra fields (2 floats, 4 ints, 2 bools), and a parser that doesn't read them derails on most UI controllers. The fork reads them for ZZZ.
- `_Model` prefabs have no NapLodController on their children. Upstream master has the whole SeparateMesh attachment, fallbacks included, inside the NapLodController check, so those children got nothing. The fork runs the candidate list above for every child.
- A repeated hash in an Avatar's TOS table made `Dictionary.Add` throw, which lost the whole Avatar, and without the Avatar there is no SeparateMesh attachment either. The fork keeps the first entry and logs the repeat at verbose level.

## Asset browser

Same-name copies: in the main asset list, assets are grouped by type and name. If loading reached one member of a group through a container id, the others are hidden. If none was reached that way, all of them stay. Nothing is removed from the loaded data, and View > "Show same-name variants" brings the full list back. The hidden copies aren't necessarily unused (Remielle's other faces belong to cutscene prefabs that ship their own), which is why they're hidden and not dropped. For a resolved asset the Container column shows the readable path instead of the hash.

This only affects meshes, since only meshes have a component that records the asset path. It's global, not per model. And "Export filtered assets" exports what's visible, so hidden copies aren't in it. "Export all assets" is unaffected.

Selection across sorts: both the main asset list and the separate Asset Browser window are virtual lists that track selection by row index, so sorting lost the selection. Both now remember the selected assets before sorting and select them again afterwards in one pass (the Asset Browser matches rows by Source plus PathID). The current row is restored and scrolled back into view. In the main list, preview decoding is paused while the selection is rebuilt.

## Rigid-bind weights in Mesh.cs

Upstream sets `weight[0] = 1 - sum` in both the BlendWeight (`case 12`) and the BlendIndices (`case 13`) handler. It looks like a copy-paste bug. It isn't.

Channels are read in ascending order. If a BlendWeight channel exists, the second line exactly undoes the first (`1 - (1 - w0) = w0`). If a mesh is rigidly bound (BlendIndices with one index per vertex, no BlendWeight channel at all), `case 12` never runs and the `case 13` line is the only thing that gives the vertex a weight of 1.

Removing that line leaves 756 of 2521 skinned meshes in the game data (690,320 vertices) with all weights at zero and valid bone indices: hats, glasses, badges, weapons, Bangboo heads. After FBX import they hang off no bone and stay at the origin when animated.

The fork writes the intent out: in `case 13`, if a vertex's weights add up to zero, `weight[0] = 1`. Real weights from `case 12` are left alone. Don't simplify this away.

## AnimatorController and Avatar layouts

ZZZ ships two layouts of each, and the serialized type hash tells them apart exactly.

- AnimatorController with hash `9860551F...` still writes `m_TypeID` in every `ValueConstant`, so each one is 16 bytes instead of 12. Reading 12 leaves the parser inside `m_Values`, and it only dies later in `m_DefaultValues` with `EndOfStreamException`. The fork reads `m_TypeID` for that hash (and for old Unity versions, as before). On the blocks that contain TerrorBird, parse errors went from 25 of 64 controllers to 0. Two controllers that used to "parse" had wrong clip counts and are now right.
- The common ZZZ Avatar ends `AvatarConstant` with `m_UseNextLevelForRootMotionSkeleton`. The layout with hash `06FC117C...` doesn't have it. Reading the flag anyway puts the parser one byte off, alignment makes it four, and the object dies later in `HumanDescription`. The fork skips the flag for that hash, unknown hashes still read it. This is what broke `Monster_TerrorBird_TurnBased` with "Transform hierarchy has been optimized, but can't find Avatar to deoptimize".

## Numbers use the invariant culture

The CLI never set a culture. On a German-locale machine every `.anim` and `.obj` got comma decimals:

```
value: {x: -0,62396765, y: 0,77544904, z: 0,060597297, w: 0,07530816}
```

In a YAML flow mapping the comma separates elements, so those files didn't parse. Now the CLI sets the invariant culture at startup, the YAML writer and the OBJ vertex, UV and normal lines use it explicitly in both the CLI and the GUI exporter, and shader property defaults (`Range(...)`, colors, vectors, floats) are written invariant too, so `= 0.25` instead of `= 0,25`. The GUI already forced en-US on its export threads, so GUI animation exports weren't affected.

## GameType values are persisted

The int value of `GameType` is written into every asset map and used to pick the game when a map is loaded in the asset browser. Upstream's enum had no explicit values, and new games were inserted in the middle of the list, which shifted everything after them. A map built by one build then selected a different game in another, decrypted with the wrong key, and failed much later with `OverflowException` or `EndOfStreamException` while loading bundles.

Every member now has an explicit value. Never renumber one, only add new games at the end. `ZZZ = 13`, `ZZZ_CB1 = 14`, `ZZZ_CB2 = 15`.

## Known limits

- The CLI loads and exports one input file at a time and clears everything in between. Anything that crosses blocks (separate meshes, avatars, dependencies) won't work there. On Cecilia's set the CLI attaches 0 of 32 separate meshes. Use the GUI, which loads everything together.
- Some Remielle avatars still get the channel-less face: `_Ramiel_Model`, `_PasSeul_Model` and the three `*_Ani_UI_MainPage_01` avatars. Their own folder has no `Remielle_Face.mesh` and nothing in the data points at the Origin face, so the fork doesn't guess.
- A renderer with a real mesh reference into a block that isn't loaded gets no substitute. Load the missing block instead.
- Mesh folder names can't be derived from avatar names beyond the `_Model` rule (`Monster_PorcelumeTerrorBird` uses `Monster_TerrorBird_Model`), so no further naming rules were added.
- The same-name filter needs a loaded model that goes through a container id. Without one, every copy stays visible.
- Old CAB maps have to be rebuilt to get V3, and V3 maps are about a third bigger.
- FBX export of a model with an optimized transform hierarchy fails if its Avatar is in a block that isn't loaded.
