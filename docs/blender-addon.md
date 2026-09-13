# Blender add-on: AnimeStudio Takes

`tools/blender/anime_studio_takes` is a small Blender add-on for FBX files exported with more than one animation clip. It switches the armature and every shape-key data-block to the same clip in one click, can play the body, face and outfit clips of a take together, and removes root motion.

It only changes which action is assigned (and, for root motion, keys on the armature object). Bone keyframes and shape-key values are left alone.

## Install

Needs Blender 4.2 or newer.

1. Grab `anime_studio_takes-<version>.zip` from the releases, or zip the `anime_studio_takes` folder yourself (it has to contain `__init__.py` and `blender_manifest.toml`).
2. In Blender go to Edit > Preferences > Get Extensions, open the dropdown in the top right and pick Install from Disk.
3. Select the zip. "AnimeStudio Takes" shows up in the add-on list. Enable it if it isn't already.

The panel is in the 3D view sidebar (press N), tab "AnimeStudio", panel "Takes". Click any part of a character, the armature or one of its meshes. The add-on works out the rest of the character from there: the root object and everything under it, plus meshes that use the armature through an Armature modifier even if they are parented somewhere else. Several characters in one file stay separate.

## Why Blender only plays one clip

Each clip in the FBX becomes its own set of actions in Blender: one for the armature and one for every mesh with shape keys. They are all imported, but Blender's FBX importer only assigns the first one of each. If you then switch the armature to another clip in the Action Editor, the face meshes keep playing the first clip. Nothing is lost, the shape-key actions just aren't switched with the armature.

The add-on groups all those actions by clip (a "take") and assigns a whole take at once. It finds the actions by the `<data-block>|<take>` names the importer writes, by what is already assigned or sitting in NLA strips, and as a fallback by the curves themselves (a curve on `key_blocks["Fac_Jaw"]` belongs to whichever shape-key block has that key). Shape keys aren't limited to face meshes, any mesh of the character with shape keys is included. Both the older one-action-per-data-block layout and the slotted actions of Blender 4.4+ work. On slotted actions it picks the slot that fits the target, and reports the data-block if none does.

## Switching takes

- The dropdown lists every take found on the character. Takes that have layered clips (see below) show `(+N)` after the name.
- Apply Take assigns the selected take to the armature and all shape-key data-blocks.
- Previous and Next step through the list (wrapping around) and apply right away.

A data-block that the new take doesn't animate gets its action cleared, so the face can't keep playing the old clip. The message after the click lists those, plus any data-block that was matched by frame range or had no fitting slot.

Push All Takes to NLA puts every take on its own NLA track, named after the take, on the armature and every shape-key data-block. The active action is cleared. Pressing it again replaces the tracks instead of adding a second set.

## Combining body, face and outfit clips

ZZZ often splits one animation into up to three clips, driven by separate layers of the animator: the body, the face, and one per outfit. The names differ by one word after the character name:

```
Avatar_..._Zhenzhen_Ani_Death            body
Avatar_..._Zhenzhen_Face_Ani_Death       face
Avatar_..._Zhenzhen_Default_Ani_Death    outfit
```

The add-on detects these groups from the names: a take that matches another take once its first word after the shared prefix is dropped, and has exactly the same frame range, counts as a layer of that take. Nothing is hardcoded. Takes like `..._Attack_Normal_01_End` are not folded in, because dropping a word doesn't land on another take.

When the selected take has layers, a box "Layered clips for this take" appears. The body is always included, and there is one toggle per extra layer (for example Default and Face). All toggles are on when you pick a take. Combine Layered Clips then plays the body plus the ticked layers together. It works from any clip of the group, so picking the face clip and pressing it does the same thing.

A data-block can only have one active action, so this uses stacked NLA tracks. The action with the most curves goes at the bottom (usually the body), smaller ones on top with Replace blending and Hold extrapolation. Layers mostly animate different channels, so each one only overrides what it drives. The tracks are named `AS| <take>`. Pressing Combine or Push All Takes to NLA again removes them first. Apply Take does not remove them, so mute or delete them in the NLA editor if you go back to single takes and see leftovers.

## Removing root motion

The "Root motion" box keeps a character on the spot.

- Flatten: which world axes to hold still. X and Y are on by default, Z is off. X and Y are the ground plane. Holding Z too would pull the body down wherever the animation leaves the ground, so jumps and lunges sink through the floor. Turn Z on only if you really want the figure pinned to one point.
- Remove Root Motion: works on the armature's active action. If there is none (for example after Push All Takes to NLA), it uses the actions in the armature's NLA strips.
- Remove in All Takes: does it for every take of the character.

The carrier bone is measured, not looked up by name. Candidates are the parentless bones and their direct children, since rigs often have a static root on top (ZZZ calls it `Bone_Root` and never animates it). Each candidate is sampled at the first and last frame, and of those that actually move, the one with the most descendants wins. On a ZZZ rig several bones under the root travel the same distance, but the body hangs off one of them with hundreds of descendants.

The travel isn't removed from the bone's curves. Each bone has its own rest orientation, so the same world movement ends up on different local axes per bone, and editing one bone would tear it away from bones next to it. Instead the add-on moves the armature object the opposite way, which moves everything together. It records the carrier's world position and the object's placement for every frame first, then sets `matrix_world` on the armature per frame and keys its location. Going through `matrix_world` lets Blender handle the conversion into the parent's space, so it still works when the armature sits under a rotated or scaled parent (like the empty of a merge export).

Details:

- The axes are world axes. The message after the click shows how far the carrier travelled on X, Y and Z. If the travel is on an axis you didn't flatten (a rig under a rotated parent can walk along world Z), turn that axis on and run it again.
- Any existing location curves on the object in that action are dropped first, so clicking twice doesn't counter the travel twice.
- NLA tracks are muted while measuring, so other strips don't blend into the pose, and restored afterwards along with the previously active action.
- Only the first armature of the character is used. Objects that aren't under the armature don't move with it.

## Blender 4.5 and older: long names

Blender up to 4.5 cuts data-block names at 63 characters. Action names are `<data-block>|<clip>|Base Layer`, and ZZZ clip names are long, so two clips like `..._ExSpecial_01` and `..._ExSpecial_02` can end up with the same cut-off action name. The add-on handles this: it treats a shorter name as a cut-off version of a longer one unless some data-block has both, and when the name can't tell two takes apart it matches them by frame range. Apply Take says so in its message ("name truncated, matched by frame range"). Newer Blender versions don't cut names this short.

## Diagnostics and Python console

If nothing is found the panel shows "No takes found" and a box with what the scan saw: objects in the character, armatures, shape-key data-blocks, actions in the file, how many were matched, and an example of an unmatched action name. Tick Show details to see the same box when takes were found. Print Details to Console writes the take list and these counts to the system console (Window > Toggle System Console).

The same functions can be called from the Python console. Installed from disk, the module normally lives under `bl_ext.user_default`:

```python
from bl_ext.user_default import anime_studio_takes as t
t.list_takes()                  # prints takes of the active object's character
t.set_take(0)                   # by index or by full take name
t.set_take_combined("Avatar_..._Ani_Death", words={"Face"})
t.push_all_takes_to_nla()
```

## Standalone scripts

Both scripts go in Blender's Text Editor and run with Run Script. They don't need the add-on.

`remove_root_motion.py` is the older script version of root motion removal. Select the armature and run it. Settings at the top: `ROOT_BONE` (default `Bip001`, set to `None` to search for the bone that travels furthest), `FLATTEN` (world axes, default `(0, 1)` for X and Y) and `ALL_ACTIONS` (False works on the active action, True goes through every action in the file). Unlike the add-on it writes `location` directly, which is only correct when the armature has no rotated or scaled parent, and on the flattened axes it overwrites the object's location rather than adding to it. Don't run it twice on the same action, the second run measures the already fixed motion and puts the travel back. It also sets the scene frame range to the action's range. Prefer the add-on button.

`diagnose_root_motion.py` shows what the root motion button sees. Click any part of the character and run it. It prints to the system console: the root object, the armatures and their parent chain, the active action and NLA strips, how far each carrier candidate travels with its descendant count, the five bones that move furthest in the whole rig, and objects that aren't under the armature (those stay behind when the object is countered). It changes nothing except the current frame.
