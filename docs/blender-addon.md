# Blender add-on: AnimeStudio Takes

When you export a character with several animations into one FBX, Blender imports all of them, but only plays the first one. Switching the animation on the skeleton doesn't switch the face, so the face keeps playing the wrong clip. This add-on fixes that and adds a few helpers.

## Install

Needs Blender 4.2 or newer.

1. Download `anime_studio_takes-<version>.zip` from the releases.
2. In Blender: Edit > Preferences > Get Extensions, the small menu in the top right, Install from Disk.
3. Pick the zip.

The panel is in the 3D view sidebar (press N), tab "AnimeStudio". Click any part of the character first.

## Switching animations

- Pick an animation in the dropdown and click Apply Take. Skeleton and face switch together.
- Previous and Next go through the list.
- Push All Takes to NLA puts every animation on its own NLA track, if you'd rather work in the NLA editor.

## Body, face and outfit together

ZZZ often splits one animation into separate clips for the body, the face and the outfit. When that's the case, a box "Layered clips for this take" shows up. Tick what you want and click Combine Layered Clips, and they play together.

## Removing root motion

Some animations move the character forward (walks, runs, dashes). To keep it on the spot:

1. In the "Root motion" box, pick the axes to hold. X and Y (the floor) are on by default. Leave Z off, otherwise jumps sink into the ground.
2. Click Remove Root Motion for the current animation, or Remove in All Takes for all of them.

The message after the click shows how far the character moved on each axis. If it still moves, turn on the axis with the big number and run it again.

## Other scripts

`tools/blender` also has two scripts you can run from Blender's Text Editor without the add-on:

- `remove_root_motion.py`: older version of the root motion button. Use the button instead.
- `diagnose_root_motion.py`: prints what the root motion button sees, for when it doesn't do what you expect.
