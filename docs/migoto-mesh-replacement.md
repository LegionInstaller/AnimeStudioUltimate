# Replacing meshes with a 3DMigoto mod

AnimeStudio can take the geometry and textures from a 3DMigoto (ZZMI) mod and put them in place of a character's meshes when you export. You keep the game's real skeleton and animations and get the mod's model on top.

It only reads the mod files from disk. Nothing touches the game, and it can't go the other way (it doesn't write mod files).

## How to use it

1. Load the character the mod is for, the same way you would for a normal model export.
2. Open Export > Replace meshes from a 3DMigoto mod...
3. Click Browse... and pick the mod folder. You can pick the folder with the ini, or a parent folder whose subfolders each hold an ini.
4. Check the table. Each row is a part of the mod, and the Replaces column says which loaded renderer it goes on. Fix anything that's wrong or empty. Leave a row on "(do not replace)" to skip it.
5. If the mod has variants, pick them in the Variants list.
6. Click Apply, then export the model (Animator, GameObject or merged GameObjects) as usual.

The window is modeless, so you can leave it open: export, look at the result, change a variant, Apply again, export again. Clear turns the replacement off. While nothing is applied, exports behave exactly as before. The preview also uses whatever is applied.

A few details:

- Rows marked "(texture only)" don't replace geometry, they repaint one material. See below.
- If you load more assets while the window is open, the new renderers show up in the dropdowns the next time the window gets focus. Picks you made by hand are kept.
- One renderer can only take one part. Assigning two to the same target gives an error on Apply.
- After an export, the mod's image files are copied next to the FBX.
- The window shows the first three warnings. All of them go to the log with a "Mesh replacement:" prefix, which is the first place to look when a mod comes out wrong.

## What's in a mod folder

A ZZMI mod is raw GPU buffers plus an ini that tells 3DMigoto what to swap:

| File | Contents |
|---|---|
| `<Part>Position.buf` | stride 40: position float3, normal float3, tangent float4 |
| `<Part>Blend.buf` | stride 32: 4 float weights, 4 uint32 bone indices (or 4 / 8 for rigid parts) |
| `<Part>Texcoord.buf` | 4 bytes vertex color, then half2 pairs |
| `<Part><Object>.ib` | index buffer, `DXGI_FORMAT_R32_UINT` (16-bit also accepted) |
| `*.dds`, `*.jpg`, `*.png` | textures |
| `*.ini` | strides, vertex counts, draw ranges, toggles, texture bindings |

Parts are found through the `Resource` sections of the ini: every resource whose file name ends in `Position.buf` starts a part, and the matching `Blend.buf` and `Texcoord.buf` are looked up by file name. Only the file name counts, not the path, so mods that sort buffers, index buffers and textures into separate subfolders work fine.

The stride comes from `stride` on the resource, or from `override_byte_stride` in a section named after the part. The vertex count comes from `override_vertex_count` in a section named after the part (the longest matching part name wins, so `Body` doesn't grab the entry for `BodyExtra`). Without one, it's taken from the file size and you get a warning.

An index buffer belongs to the part whose name its file name starts with (again, longest match). An `.ib` with no vertex buffers of its own can't be replaced and is reported.

In the texcoord buffer, the first half2 pair is the texture UV and goes to UV channel 0. The last pair goes to channel 1. The pairs in between hold outline data for the toon shader and are skipped.

The readers check themselves instead of guessing. Unknown strides are refused. If more than 5% of sampled normals aren't unit length, or more than 5% of vertices have weights that don't sum to 1, there's a warning. Buffers with fewer vertices than declared are refused, extra vertices are ignored with a warning.

`Position.buf` files that no ini mentions are reported as leftovers and ignored. Hand-edited mod folders often keep old buffers around.

## Bone indices

This is the core of the whole thing. `BLENDINDICES` in the blend buffer point straight into `m_Bones` of the target `SkinnedMeshRenderer`. Index 12 is `m_Bones[12]` of that renderer, nothing more.

So the mod has no skeleton of its own. The bone list and the bind poses come from the mesh being replaced. That's why the target has to be a skinned renderer, and why its bind pose count must match its bone count (otherwise the replacement exports unskinned, with a warning).

It also means a part only fits a renderer with more bones than its highest index. If you put a part on the wrong renderer, you'll usually get "weights bone N, but the renderer has only M" and nothing is replaced. If the bone count happens to fit, the export works but the skin is attached to the wrong bones, and it shows the moment you play an animation.

## Coordinates and UVs

Positions need no axis conversion. A Migoto buffer is in the same space as Unity's mesh data. The builder applies the same handedness flip the converter does for any Unity mesh: X negated on position, normal and tangent, triangle winding reversed, bind poses mirrored.

If you're comparing mod vertices against the game by hand, compare against the shipped mesh vertices, not against bone positions. The bone transforms are in a different space than the mesh, and a comparison against them suggests an axis swap that isn't there.

UVs do need one change: v is flipped (v -> 1-v) on both channels. The buffer stores them the Direct3D way, with v = 0 at the top of the image, while FBX puts v = 0 at the bottom. Without the flip the texture is mirrored top to bottom. On a mostly dark texture that can look almost right; on a busy one it's obvious.

## Matching mod parts to renderers

The ini names its targets by hashes like `hash = 785b21f5`. Those are taken from GPU buffers while the game runs and don't exist in any shipped asset, so the ini can't be used for matching. Names don't line up directly either: the mod says `Body` and `Legs`, the game says `Body_1` and `Body_2`.

So the window does this:

- It lists every loaded skinned renderer by GameObject name, with its bone count.
- Renderers with too few bones for the part are dropped.
- The rest are ranked by shared words. Names are split on `_`, space, `-`, `.`, `/`, camelCase humps and letter/digit boundaries. Pure numbers and one-letter words are ignored. Words come from both the part name and the mod (ini or folder) name.
- A shared word only counts if it's rare among the candidates: it has to appear in fewer than a quarter of them. The character's name is rare, `Body` is not. With fewer than 4 candidates every word counts.
- Ties go to the renderer with fewer bones.

A target is only preselected when the best candidate shares at least one rare word. If nothing is preselected, the character the mod is for probably isn't loaded, and every candidate is just some renderer with enough bones. Load the character or pick the target yourself.

The mapping is stored by renderer name, so it survives reloading assets. It also means every renderer with that name in an export gets the part.

## Unused bone influences

Some mods add a full extra influence on a bone the vertex doesn't need: the other three weights already sum to 1, and the extra one is on top. Those vertices then sum to 2, and the index can be one past the end of the target's bone list, which would rule out the right renderer.

When counting how many bones a part needs, the highest index is dropped as long as every vertex that uses it is already fully weighted (other weights sum to 0.99 or more) without it. Then the next highest is checked the same way. When the mesh is built, influences that point past the target's bone list are removed and reported. The "Needs bones" column shows the count after this.

## Draw ranges and variants

Every `drawindexed = count, start, basevertex` becomes one submesh. `drawindexed = auto` means the whole index buffer. A count of 0 is a placeholder and is dropped. Triangles that point outside the vertex buffer are dropped with a warning.

Draws inside `if` blocks belong to variants. The variables and their values come from the ini:

```ini
[KeySwapBody]
$body = 0,1,2,3
```

Variables that only appear in conditions get the values they're compared against, plus 0. A variable with no known values is treated as on/off (0, 1). The first value is the default. The Variants list only shows variables that some draw or texture actually depends on, because inis also declare variables for their own on-screen menu. `$active` is hidden and always 1.

`if` / `else if` / `else` is read as a chain: an `else if $body == 1` means "`$body != 0` and `$body == 1`". Nested `if`s combine with "and". Only `$name == value` and `$name != value`, joined by `&&`, are understood. Anything else (`||`, comparisons on built-ins) is treated as always true, so you may get extra geometry, but never lose geometry to a misread condition.

The check that draws fit their index buffer runs per draw, not on the sum. Branches of an `if` exclude each other, so their total is often larger than the buffer, and that's fine.

## The ini runs like a program

A section is not a unit. Reading goes the way 3DMigoto executes it:

- Start at each `TextureOverride*` section, in file order.
- `run = CommandListX` jumps into that section and comes back. A list that ends up running itself is stopped.
- `ib = ResourceX` sets the current index buffer. It's state, not a property of the section. `ib = null` or an unknown name unbinds it.
- A `drawindexed` belongs to whatever `ib` was set last.
- `Resource\ZZMI\Diffuse = ref ResourceX` binds a texture, together with the conditions it sits in.

```ini
[CommandListDrawBody]
ib = ResourceLegIB
drawindexed = 38802, 13656, 0
ib = ResourceBodyIB
drawindexed = 73665, 0, 0
```

Here the first draw goes on the leg buffer and the second on the body buffer, even though both are in the same section.

Several overrides often run the same command list (a low quality variant of the same piece, for example). A draw with the same buffer, range and conditions is only kept once.

## Several inis

Bigger mods ship one folder per piece, each with its own ini. When you pick a folder, all inis in it are read. If it has none, all inis in the folders directly below it are read (one level only).

Each ini is read against its own folder, so its parts point at its own files. The results are merged. If two inis both have a part with the same name, the second one gets the ini name in front, like `Sword/Body`. The leftover-file check runs once over the whole folder, so inis sharing a folder don't report each other's buffers. An ini that fails to parse is skipped with a warning.

## Blend buffer layouts

- Stride 32: four float weights, then four uint32 bone indices. The normal case for bodies, hair and so on.
- Stride 4: one uint32 bone index per vertex, no weight. The vertex is fully weighted to that bone. Used for rigid props like weapons.
- Stride 8: the same uint32 index first, then a field whose meaning isn't known. It's ignored, the vertex is fully weighted to the index, and there's a warning.

Any other stride is refused.

## Textures

A mod's geometry is UV-mapped for the mod's own textures, not the game's. The game texture on mod geometry doesn't just look a bit off, it's a different layout.

So for each index buffer, the diffuse and normal map the ini binds become a material called `MOD_<object>`, with the diffuse on channel 0 and the normal map on channel 1. The slot is the last part of the binding key (`Diffuse`, `NormalMap`). When a slot is bound more than once under different conditions, the last binding that matches your variant choice wins, same as in the game.

If the ini binds nothing for an object, or the image can't be read, that submesh keeps the original material. Light maps and material maps aren't linked (they're for the game's toon shader, which no importer rebuilds), but they're copied next to the FBX with the other image files.

The FBX writer saves every texture as `<name>.png`, so images are actually decoded:

- DDS with a DX10 header: BC1, BC3, BC4, BC5, BC6H, BC7, uncompressed BGRA8 and RGBA8.
- Older DDS: DXT1, DXT5, ATI1/BC4U, ATI2/BC5U, and uncompressed 32-bit with BGRA or RGBA masks.
- JPG and PNG go through SkiaSharp.

Only the first mip of a DDS is used. Anything else is reported and the original material stays.

Alpha is forced to fully opaque. In ZZZ the diffuse alpha is a mask for the toon shader, not transparency. Blender wires alpha to transparency, and whole limbs disappear if it's left in.

## Texture-only overrides

Some overrides bind textures and draw nothing. Faces are usually done this way, because a replaced face mesh loses its blend shapes (3DMigoto only sees the finished buffer). The mod changes the look and keeps the geometry.

```ini
[TextureOverrideFaceA]
hash = dbd59d30
run = CommandListFaceA

[CommandListFaceA]
Resource\ZZMI\Diffuse = ref ResourceFaceADiffuse
```

An override that binds images, including a diffuse, but reaches no draw shows up as `<name>  (texture only)`. Its target is a renderer and material pair, like `Face / MAT_Face`, because a face renderer often has a second material for the eyebrows that must stay as it is. Every material of every loaded renderer is offered, ranked by name the same way.

On export that one material is swapped for `MOD_<name>`. Geometry, blend shapes and the other materials are left alone.

## Limits

- Replaced geometry has no blend shapes. Use a texture-only override for faces.
- Only skinned renderers can take replaced geometry.
- Only the diffuse and normal map are linked into materials.
- Only the ZZMI position layout (stride 40) and the three blend layouts above are read.
- Conditions it can't parse count as true, so a mod with complex conditions may export with extra pieces.
- If the ini binds no textures, the game's material stays, and it won't fit the mod's UVs.
- Blender's FBX importer only picks up the first UV layer. That's the same for normal exports.
- It doesn't write mod files.
