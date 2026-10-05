# Blender add-on: AnimeStudio Takes

When you export a character with several animations into one FBX, Blender imports all of them, but only plays the first one. Switching the animation on the skeleton doesn't switch the face, so the face keeps playing the wrong clip. This add-on fixes that, can build combos the way the game plays them, and adds a few helpers.

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

## Building combos

You type in the button presses, the add-on plays them through the game's own animator and lays the clips out in the NLA editor with the game's timing. Needs Blender 5.0 or newer.

1. In AnimeStudio, export the character with "Export animations" on. Next to the FBX you get a `<name>.animator.json` with the animator's states and transitions.
2. Import the FBX into Blender and click a part of the character.
3. In the "Combo" box, pick that `.animator.json`.
4. Click the inputs in order, for example PressAttackA three times. They show up in the text field below.
5. Click Build Combo.

Every input comes at the earliest frame the game accepts it, like a perfect player. Clips that follow on their own (an attack's `_End` clip, the way back to Idle) are added too. For Pulchra, PressAttackA three times gives exactly the chain from [Rebuilding ZZZ combos in Blender](zzz-animator-controllers.md).

More in the text field:

- `PressEvade PressAttackA+PerfectEvade`: two inputs in the same moment, joined by `+`.
- `Int_BranchIndex=1`: set a value from that point on, here the ExSpecial instead of the Special.
- `wait`: let the current clip play out before the next input.
- `wait:256`: let 256 frames pass. Needed for things the game drives with an on/off value instead of a button, like walking.

Walking, for example the TerrorBird: `Int_MoveType=0 Bool_IsMoving=1 wait:256 Bool_IsMoving=0`. That's Idle into Walk (15 frames blend), two steps, then back to Idle (6 frames blend). Like in the game, the bird always finishes its step before it stops, even if you set `Bool_IsMoving=0` in the middle of one. `Int_MoveType` decides between walking (0) and running (1); the TerrorBird's controller starts on 1. If nothing turns the value off again, the combo ends where the walk would repeat forever and says so.

Each clip is its own strip, and every clip starts its root motion from its own beginning. "Carry root motion" (on by default) makes up for that: it moves the whole character along with the clips, so walks and attack chains cover ground like in the game instead of jumping back at every new clip. Only the ground is carried, so jumps still land. The movement sits on its own NLA track, "AS| carry root motion", on the topmost object of the character. The message after Build Combo says how far the character got.

If an input isn't possible at that point (for example a move the character can't do from where it is), the add-on says where the chain broke off.

If the combo needs a clip that isn't in the imported FBX, or one that wasn't loaded in AnimeStudio when you exported, nothing is built and your scene stays as it was. The message lists the clips, so you know what to export again. "Show all inputs" also offers the triggers the game sets itself, like Hit. "Idle" is how long the start state plays before the first input, and "Start" picks a different start state.

Blender before 5.0 cuts clip names at 63 characters, so clips of one character can't be told apart reliably. That's why combos need 5.0.

## Other scripts

`tools/blender` also has two scripts you can run from Blender's Text Editor without the add-on:

- `remove_root_motion.py`: older version of the root motion button. Use the button instead.
- `diagnose_root_motion.py`: prints what the root motion button sees, for when it doesn't do what you expect.
