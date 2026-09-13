# Fixes compared to upstream

Things that were broken or missing in upstream AnimeStudio for Zenless Zone Zero, and are fixed here.

## Loading

- Files went missing when loading many ZZZ files at once. The game uses the same internal name for different files, and upstream only kept the first one. Now all of them are kept.
- Asset maps had the same problem. Rebuild your old maps to get the fix. Old maps still open.
- Some `_UI` and `_Model` characters failed to load their animator or meshes. Both work now.
- Some enemy animators and avatars (TerrorBird for example) failed to read. Fixed.

## Meshes and shape keys

- Some characters exported without facial shape keys, Remielle for example. The game has several face meshes with the same name, and upstream picked whichever loaded first. Now the right one is picked.
- Shape key animation could end up on the wrong mesh when two meshes had shape keys with the same name. Fixed.
- Hats, glasses, weapons and similar rigid parts keep their bone binding.

## Asset list

- Duplicate meshes with the same name are hidden when the right one is known. View > "Show same-name variants" shows all of them again.
- Sorting the list no longer loses your selection.

## Export

- On Windows with German (or other comma decimal) settings, the CLI wrote numbers like `0,25` into `.anim` and `.obj` files, which broke them. Fixed.
- Asset maps could select the wrong game when opened with a different build. Fixed.

## Known limits

- The CLI loads one file at a time, so characters split over several files don't export correctly there. Use the GUI for characters.
- A few of Remielle's alternate outfits still export without face shape keys, because the game data doesn't say which face they use.

## For developers

- In `Mesh.cs`, the weight line in the BlendIndices case looks like a duplicate. It isn't. Removing it breaks the skinning of hundreds of rigid meshes.
- `GameType` values are saved in asset maps. Never renumber them, only add new games at the end.
