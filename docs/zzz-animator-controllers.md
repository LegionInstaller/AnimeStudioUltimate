# Rebuilding ZZZ combos in Blender

In the game, an attack combo isn't one animation. It's several clips that follow each other, and the next one starts before the last one is done. These notes show how to rebuild that in Blender with the NLA editor. The numbers come from the game's own animator data.

The [Blender add-on](blender-addon.md#building-combos) can do this for you: type in the inputs and it lays the clips out with these same numbers. The rest of this page explains what it does.

Set the scene to 60 fps first. All ZZZ clips are 60 fps and the frame numbers below depend on it.

## Example: Pulchra's normal attack

| Clip | Starts at frame | Blend in |
|---|---|---|
| `Idle_Loop` | before 0 | |
| `Attack_Normal_01` | 0 | 6 |
| `Attack_Normal_02` | 20 | 6 |
| `Attack_Normal_03` | 67 | 6 |
| `Attack_Normal_03_End` | 137 | 0 |
| `Idle_Loop` | 349 | 6 |

Put each clip on its own NLA track, each one above the previous, with blending set to Replace. Let every clip run to its full length even when the next one has already started. That's what the game does too.

A few rules:

- Blend in is 6 frames between attacks, 3 frames when going into a special attack.
- No blend (0) between a clip and its `_End` clip. They're one motion cut in two, and a blend would look doubled.
- From idle, only start with `Attack_Normal_01` or a special. `02`, `03` and the `_End` clips start mid swing and look wrong on their own.

A single hit with no follow-up: `Attack_Normal_01` at 0, `Attack_Normal_01_End` at 60 with no blend, idle at 318.

## Example: TerrorBird walking

Start walking: put `TerrorBird_Ani_Walk_F` on a track above `TerrorBird_Ani_Idle`, starting wherever you like, blend in 15.

Stop walking: the bird always finishes its step first. Let the walk clip run all the way to the end (128 frames), then put idle on the track above with blend in 6. Make the walk strip a few frames longer than the clip (hold the last frame), so the idle has something to blend from.

The walk moves the bird forward on its own. If you loop it, you have to move it forward yourself for every loop, or remove root motion with the [Blender add-on](blender-addon.md).

## For developers

ZZZ adds frame counts to every transition (`m_FrameCount`, `m_TotalFramesSrc`, `m_UseFrameCount` and a few more). AnimeStudio keeps them on `TransitionConstant` in `AnimeStudio/Classes/AnimatorController.cs`. They matter, because the normal Unity exit time is sometimes out of date and the frame count is right. Blend durations are in seconds. `AnimeStudio.Utility/AnimatorGraph.cs` turns all of it into frames for the `.animator.json`, and `tools/blender/anime_studio_takes/combo.py` plays a combo through it (it runs without Blender too: `python combo.py <file>.animator.json "PressAttackA PressAttackA"`).
