"""Takes the travel out of a ZZZ animation without pulling the figure apart.

Usage in Blender: select the armature, open this script in the Text Editor, Run.
It works on the active action; set ALL_ACTIONS = True to do every action.

Why the object and not the bone curves
--------------------------------------
The obvious fix would be to delete the location curves of the carrier bone. That goes
wrong on these rigs. Measured on Remielle's beach run:

* Under the armature root `Bone_Root` there are nine bones side by side: `Bip001`
  and eight floaters. They all travel the same 0.0454 units.
* Delete only `Bip001` and the hips stay put while the floaters keep going.
* Subtracting `Bip001`'s curve from the others doesn't work either: every bone has its
  own rest orientation, so the same world movement lands on different local axes.
  `Bip001` carries it on local x (4.54), a floater splits it over x (3.07) and
  y (3.34). Both come out as the same 0.0454 in world space.

Countering on the object avoids all of that, because it moves everything together.
Measured afterwards, the hip to floater distance stays the same to five digits.
"""

import bpy

ROOT_BONE = "Bip001"      # carries the figure; None = search for it
FLATTEN = (0, 1)          # hold world X and Y. Z stays, otherwise jumps go flat
ALL_ACTIONS = False       # True: every action of the armature, one after another


def _root_bone(arm):
    """The bone that travels furthest, which is the one carrying the figure."""
    if ROOT_BONE and ROOT_BONE in arm.pose.bones:
        return ROOT_BONE
    scene = bpy.context.scene
    best, best_len = None, 0.0
    tops = [b.name for b in arm.data.bones if b.parent is None or b.parent.parent is None]
    for name in tops:
        scene.frame_set(scene.frame_start)
        a = (arm.matrix_world @ arm.pose.bones[name].matrix).translation.copy()
        scene.frame_set(scene.frame_end)
        b = (arm.matrix_world @ arm.pose.bones[name].matrix).translation.copy()
        if (b - a).length > best_len:
            best, best_len = name, (b - a).length
    return best


def remove_root_motion(arm, action):
    scene = bpy.context.scene
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = action
    if getattr(action, "slots", None):
        ad.action_slot = action.slots[0]

    first, last = (int(round(v)) for v in action.frame_range)
    scene.frame_start, scene.frame_end = first, last
    bone = _root_bone(arm)
    if bone is None:
        print("no carrier bone found")
        return

    # Measure the path first, then counter it. Keying while measuring would skew it.
    path = {}
    for f in range(first, last + 1):
        scene.frame_set(f)
        path[f] = (arm.matrix_world @ arm.pose.bones[bone].matrix).translation.copy()
    start = path[first]

    for f in range(first, last + 1):
        scene.frame_set(f)
        for axis in FLATTEN:
            arm.location[axis] = -(path[f][axis] - start[axis])
        arm.keyframe_insert("location", frame=f)

    moved = (path[last] - start).length
    print(f"{action.name}: removed {moved:.4f} units of travel from '{bone}' "
          f"({last - first + 1} frames)")


def main():
    arm = bpy.context.object
    if arm is None or arm.type != 'ARMATURE':
        print("select the armature first")
        return
    actions = [arm.animation_data.action] if not ALL_ACTIONS else list(bpy.data.actions)
    for action in actions:
        if action is not None:
            remove_root_motion(arm, action)


main()
