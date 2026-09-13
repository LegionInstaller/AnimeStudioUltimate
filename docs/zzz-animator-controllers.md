# ZZZ animator controllers

Notes on how Zenless Zone Zero AnimatorControllers chain clips together, and how to rebuild
those chains in Blender. The two examples are the playable agent Pulchra
(`Avatar_Female_Size03_Pulchra_Controller`) and the TerrorBird enemy
(`Monster_TerrorBird_Controller`). All values come straight from the controller data and the
clips. Nothing here is guessed from clip names.

The enemy called Pulchra (`Monster_Pulchra_*`) has a different controller and isn't covered.

## Layout of a controller

The parsing lives in `AnimeStudio/Classes/AnimatorController.cs`. The pieces:

- `ControllerConstant` holds a list of layers and a list of state machines.
- `LayerConstant` points at one state machine and has a blending mode (Override or Additive),
  a default weight and a skeleton mask.
- `StateMachineConstant` has the states, the AnyState transitions and the selectors.
- `StateConstant` is one state with its motion, speed, cycle offset and outgoing transitions.
- `SelectorStateConstant` is an Entry or Exit node of a sub state machine.
- `TransitionConstant` has the destination, duration, offset, exit time, flags and a list of
  `ConditionConstant` (mode, parameter hash, threshold).

Unity flattens sub state machines into one state list. The nesting only survives in the
selectors. A transition from a state to its Exit selector, then through an Entry selector, is
how you get from one sub machine into another. Selectors take no time, so the transition that
actually matters is always the one on the source state.

Both controllers have three layers. Layer 0 (Base Layer) holds everything interesting. The
stored weight of layer 0 is 0 on TerrorBird, but Unity always treats the base layer as 1. The
other two are a HitShake layer (Additive, weight 1, only plays `Hit_Shake` on
`Trigger_Hit_Shake`) and an SE layer (Override, weight 1). On Pulchra the SE layer is empty.
The order of those two differs between controllers. No layer has an AvatarMask.

Every state is a BlendTree with exactly one leaf, which is just how Unity stores a plain clip
state. Pulchra has no real 1D or 2D blend trees at all, locomotion is split into separate
states. TerrorBird has two real ones (`MoveLeft`, `MoveRight`, Simple1D on a float called
`NormalizedTime`), but they are not part of the walk example below.

Speed is 1 and cycle offset 0 almost everywhere (Pulchra's `Revive_01..04` play backwards at
speed -4). Speed and cycle offset parameters are unused on the base layer.

## How ZZZ stores times

This is the part that trips you up. ZZZ added eight fields to `TransitionConstant`:

```
m_AutoTransitionOffsetValue  m_AutoTransitionOffsetRatio  m_AutoTransitionOffset
m_FrameCount  m_TransitionOffsetCount  m_TotalFramesSrc  m_TotalFramesDest  m_UseFrameCount
```

AnimeStudio reads them so the stream stays aligned, but throws them away (they are locals, not
fields on the class). You need them though, because on Pulchra 558 of 575 transitions have
`m_UseFrameCount = True`. The 17 without it are the ones with no time gate at all (AnyState,
Entry, HitShake layer).

What that means in practice:

1. The normal Unity `m_ExitTime` (normalized, 0 to 1) is stale in places. `Attack_Rush ->
   Attack_Rush_End` says exit time 0.625, which would be frame 62 of 100. `m_FrameCount` says
   100 of 100. The first frame of `Rush_End` matches frame 99 of `Rush` (0.98 degrees off) and
   not frame 62 (17.82 degrees off). Same story for `Attack_Counter` and `Attack_Special_01`.
   Trust the frame count. `m_TotalFramesSrc` and `m_TotalFramesDest` match the real clip
   lengths in every case checked.
2. Blend durations are seconds, not a fraction of the clip. `m_HasFixedDuration` is true on all
   575 Pulchra transitions. All clips here are 60 fps, so 0.1 s = 6 frames, 0.05 s = 3 frames,
   0.25 s = 15 frames.
3. Most conditions are frame windows. The controller declares an int parameter `FrameCount`
   that the game sets to the current frame of the running clip. Condition mode 4 (Less) is the
   upper bound. Mode 9 is not a standard Unity mode; it is the lower bound (185 uses, while mode
   3 Greater is never used). `Evade_Front -> Attack_Rush` has `FrameCount mode9 4` and
   `FrameCount Less 25`, so the window [4, 25). Whether mode 9 means `>=` or `>` can't be told
   from the data; it's one frame either way. Where the old exit time was kept in sync, it is
   exactly threshold / clip length (for the 70 frame `Attack_Normal_03`, 21/70 = 0.3).

Transition offsets are almost never used. Only 4 of 575 transitions have a nonzero
`m_TransitionOffset` and 7 a nonzero `m_TransitionOffsetCount`, and all of them go into
locomotion (for example `Evade_Front -> Run_Loop` starts the run at frame 12 of 28). Every
transition into an attack state has offset 0.

## Example: Pulchra attack chain

The chain is Idle -> `Attack_Normal_01` -> `02` -> `03` -> `03_End` -> Idle.

Only `Attack_Normal_01` can be entered from outside. The Attack_Normal sub machine's Entry
selector has two exits and both lead to `Normal_01`. `Normal_02` is only reachable from
`Normal_01` and `Attack_Rush`, `Normal_03` only from `Normal_02`, `Attack_Counter` and
`Attack_BeHitAid`. That matches the poses: the first frame of `Normal_01` is 0.01 degrees
from Idle, while `Normal_02` starts 26.12 degrees away and `Normal_03` 20.88. Those clips begin
mid swing on purpose, and not because of an offset.

There is no combo counter. The position in the combo is simply which state you are in, and
the trigger is `Trigger_PressAttackA`. `Normal_03 -> Normal_01` also exists as a combo loop
(0.1 s, FrameCount >= 60 of 70).

| Step | Blend | Gate | Start frame (fastest combo) |
|---|---|---|---|
| Idle -> Normal_01 | 0.1 s (6 f) | none, fires on the trigger | 0 |
| Normal_01 -> Normal_02 | 0.1 s (6 f) | FrameCount >= 20 of 60 | 20 |
| Normal_02 -> Normal_03 | 0.1 s (6 f) | FrameCount >= 47 of 80 | 67 |
| Normal_03 -> Normal_03_End | 0 s | exit at frame 70 of 70 | 137 |
| Normal_03_End -> Idle | 0.1 s (6 f) | exit at frame 212 of 212 | 349 |

The zero blend between a clip and its `_End` is correct. `Attack_X` and `Attack_X_End` are one
motion cut into two clips: the last frame of the base clip and frame 0 of `_End` are within 0.21
to 2.80 degrees of each other. Blending there would give you a double image. The other two
`_End` clips work the same way: `Normal_01 -> Normal_01_End` at frame 60 with 0 s, then back to
Idle at frame 258 with 0 s. `Normal_02 -> Normal_02_End` at frame 80 with 0 s, then Idle at 276
with 0.1 s.

Every `_End` clip lands back on the Idle pose (0.10 to 0.33 degrees) and the Idle loop itself is
closed (frame 239 vs 0: 0.11 degrees), so it doesn't matter where in Idle the attack starts.

Blending into a special from a running attack uses 0.05 s (3 frames) instead of 0.1 s. That is
the only place the blend length depends on context.

## Blender recipe: attack chain

Scene at 60 fps. One NLA track per link, each higher track above the previous one, blending
Replace, extrapolation Hold only on the last strip.

- Track 1: `Idle_Loop` up to frame 0.
- Track 2: `Attack_Normal_01` at 0, ends 60, blend in 6.
- Track 3: `Attack_Normal_02` at 20, ends 100, blend in 6.
- Track 4: `Attack_Normal_03` at 67, ends 137, blend in 6.
- Track 5: `Attack_Normal_03_End` at 137, ends 349, blend in 0.
- Track 6: `Idle_Loop` at 349, blend in 6.

The strips overlap on purpose. `Normal_01` keeps running to 60 even though `Normal_02` takes
over at 20. That's what Unity does too, and the upper track wins fully once its blend in is
done. Always use blend in 6 (or 3 for specials), never a percentage. Keep blend in 0 between a
clip and its `_End`.

A single hit without follow-up: `Normal_01` 0 to 60, `Normal_01_End` 60 to 318 with blend in 0,
then Idle at 318 with blend in 0.

Don't start `Normal_02`, `Normal_03`, `Attack_Counter` or any `_End` clip on its own from Idle.
From Idle, only `Attack_Normal_01`, `Attack_Special_01` and `Attack_ExSpecial_01` make sense.

## Example: TerrorBird Idle -> Walk and back

Going out: `Idle` has a transition on `Bool_IsMoving` to Exit selector 21, which goes through
Entry selector 22 and `Int_MoveType == 0` to `Walk`. Coming back: `Walk` has an unconditional
transition at exit time 1.0 to Exit selector 23. That selector checks `Trigger_Hit`,
`Trigger_PressAttackA` and `Bool_IsMoving` in order, and if none match it falls through to Entry
selector 20, which leads to `Idle`.

| | Idle -> Walk | Walk -> Idle |
|---|---|---|
| Duration | 0.25 s = 15 frames | 0.1 s = 6 frames |
| Fixed duration | true | true |
| Has exit time | false (0.625 is stored but unused) | true, exit time 1.0 |
| Condition | `Bool_IsMoving` | none |
| Offset | 0, Walk starts at frame 0 | 0, Idle starts at frame 0 |

There is no transition out of `Walk` on `Bool_IsMoving == false`. The bird always finishes the
step cycle before it can stop. So going out can start anywhere in Idle, but coming back always
starts at the end of the walk cycle.

The clips are `TerrorBird_Ani_Idle` (160 frames, loops) and `TerrorBird_Ani_Walk_F` (128
frames, clip `m_LoopTime` false). Root motion is baked in: `Root` and `Bip001` move 4.5936 units
forward in +Z over the walk, about 0.0359 per frame. Idle stays in place. `Bip001` sits at Y
2.284 in the walk and 2.132 in idle.

Why 6 frames are enough to stop:

- `Walk_F` is a closed cycle. Frame 128 vs frame 0 is 0.03 degrees on average. Combined with exit
  time 1.0, the blend always starts from the exact same pose, never mid step.
- At that point the skin bones (`Skn_*`) are already close to idle, 2.30 degrees on average.
- The remaining difference is almost all in the left leg (`Bip001 L Foot` 53.7 degrees, `L Toe1`
  50.6, `L Thigh` 25.3). The right leg is nearly in idle already. Six frames to plant one foot
  reads as a last step, not a pop.
- The walk does not settle towards idle by itself (6.60 degrees from idle at frame 0, 6.63 at
  frame 128). The smoothness comes from the points above.

Controller revisions differ. `Monster_TerrorBird_Controller_TurnBased` plays `Walk` at speed 1.7
and blends back with 0.306 s. Check the controller you actually use.

## Blender recipe: locomotion

Scene at 60 fps, otherwise the durations are off.

Idle -> Walk, with X as the frame where the bird starts moving (any frame is fine):

- Track 1: `TerrorBird_Ani_Idle` from frame 1, repeated until at least X + 15.
- Track 2: `TerrorBird_Ani_Walk_F` starting at X, action start 0, blending Replace,
  extrapolation Hold, blend in 15.

Walk -> Idle, with W as the frame where the walk strip starts:

- Walk strip from W to W + 134, 6 frames longer than the clip so there is something to blend
  against. Repeating a bit or holding the last frame looks the same here.
- `TerrorBird_Ani_Idle` on the track above, starting at W + 128, action start 0, blend in 6,
  then looping.

If you want it exact instead of relying on blend in, set `use_animated_influence` on the strip
and key `influence` linearly: 0.0 at the blend start, 1.0 at the blend end. Unity's crossfade is
linear.

Things that go wrong:

- Starting the stop blend before W + 128. The game can't switch mid step, so you get a pose mix
  that never happens in game.
- Ending the walk strip at W + 128. Then the idle blends against nothing and pops in.
- Leaving the root channels out of the blend. The slow down comes from the forward root motion
  fading out over the blend frames, and the body dropping from Y 2.284 to 2.132.
- Looping the walk strip without carrying the root forward. The clip isn't built as a loop, so
  you have to add the 4.5936 offset per pass yourself.
