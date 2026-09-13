# Exporting a character with a 3DMigoto mod

You can export a character with the meshes and textures of a 3DMigoto (ZZMI) mod instead of the original ones. The skeleton and animations still come from the game, so the mod's model moves like the real character.

It only reads the mod folder. Nothing is written to the mod and nothing touches the game.

## How to use it

1. Load the character the mod is made for, like you would for a normal export.
2. Open Export > Replace meshes from a 3DMigoto mod...
3. Click Browse... and pick the mod folder. If the mod is split into several folders, pick the folder above them.
4. Check the table. Each row is a part of the mod, and "Replaces" shows which part of the character it goes on. Fix wrong or empty rows. Set a row to "(do not replace)" to skip it.
5. If the mod has outfit toggles, pick them in the Variants list.
6. Click Apply and export as usual.

You can leave the window open. Export, check the result, change something, Apply again, export again. Clear turns it off.

The mod's texture files are copied next to the exported FBX.

## Faces

Rows marked "(texture only)" only change the textures of one part and keep the original mesh. Mods usually do faces this way, because a replaced face mesh has no shape keys. So facial animation still works with these.

Replaced meshes never have shape keys.

## If something looks wrong

Nothing is picked in "Replaces": the character the mod is for probably isn't loaded. Load it, or pick the parts by hand.

Error "weights bone N, but the renderer has only M": the row is set to the wrong part of the character.

The mesh looks fine but twists when animated: also the wrong part. It happened to have enough bones to export, but they're the wrong bones.

Extra pieces show up: the mod probably uses toggles that aren't read correctly. Try the Variants list, or set that row to "(do not replace)".

The texture looks wrong: only the base color and the normal map are hooked up. Other maps are copied next to the FBX but not connected. Transparency is removed on purpose, because in ZZZ that channel isn't transparency and Blender would hide parts of the body.

All warnings go to the log with a "Mesh replacement:" prefix. That's the first place to look.

## Limits

- Only skinned parts of a character can be replaced (body, hair, clothes), not static props.
- Complicated mod toggles can bring in extra pieces.
- Blender only imports the first UV map. That's the same for normal exports.
