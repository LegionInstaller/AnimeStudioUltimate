"""Nimmt die Wanderung aus einer ZZZ-Animation, ohne die Figur zu zerlegen.

Benutzung in Blender: Armature auswaehlen, dieses Skript im Text-Editor oeffnen, Run.
Es wirkt auf die aktive Action; fuer alle Actions ALL_ACTIONS = True setzen.

Warum ueber das Objekt und nicht ueber die Knochenkurven
--------------------------------------------------------
Naheliegend waere, die location-Kurven des tragenden Knochens zu loeschen. Das geht bei
diesen Rigs schief. Gemessen an Remielles Beach-Run:

* Unter der Armature-Wurzel `Bone_Root` haengen **neun** Knochen nebeneinander: `Bip001`
  und acht Floater. Sie wandern alle gemeinsam 0.0454 Einheiten.
* Loescht man nur `Bip001`, bleibt die Huefte stehen und die Floater fliegen weiter.
* Zieht man `Bip001`s Kurve von den anderen ab, stimmt es auch nicht: jeder Knochen hat
  seine eigene Ruheorientierung, dieselbe Weltbewegung steckt bei jedem in anderen lokalen
  Achsen. `Bip001` traegt sie auf lokal x (4.54), ein Floater verteilt auf x (3.07) und
  y (3.34) -- beide ergeben dieselben 0.0454 in der Welt.

Die Gegenbewegung auf dem **Objekt** umgeht das ganze Problem: sie verschiebt alles
gemeinsam. Nachgemessen bleibt der Abstand Huefte<->Floater auf fuenf Stellen gleich.
"""

import bpy

ROOT_BONE = "Bip001"      # traegt die Figur; None = automatisch suchen
FLATTEN = (0, 1)          # Welt-X und -Y neutralisieren. Z bleibt, sonst fallen Spruenge flach
ALL_ACTIONS = False       # True: jede Action der Armature nacheinander


def _root_bone(arm):
    """Der Knochen, der am weitesten wandert -- das ist der, der die Figur traegt."""
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
        print("kein tragender Knochen gefunden")
        return

    # Erst den Weg messen, dann gegensteuern -- waehrend des Setzens waere er verfaelscht.
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
    print(f"{action.name}: {moved:.4f} Einheiten Wanderung von '{bone}' aufgehoben "
          f"({last - first + 1} Frames)")


def main():
    arm = bpy.context.object
    if arm is None or arm.type != 'ARMATURE':
        print("bitte die Armature auswaehlen")
        return
    actions = [arm.animation_data.action] if not ALL_ACTIONS else list(bpy.data.actions)
    for action in actions:
        if action is not None:
            remove_root_motion(arm, action)


main()
