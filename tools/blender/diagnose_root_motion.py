"""Shows what the root motion button sees in THIS file.

Usage: click any part of the character, open this script in the Text Editor, Run.
Output goes to the system console (Window > Toggle System Console).
"""

import bpy


def line(*parts):
    print("[RM]", *parts)


obj = bpy.context.object
if obj is None:
    line("no active object")
else:
    root = obj
    while root.parent is not None:
        root = root.parent
    family = [root] + list(root.children_recursive)
    arms = [o for o in family if o.type == 'ARMATURE']

    line("active object :", obj.name, f"({obj.type})")
    line("root object   :", root.name)
    line("objects total :", len(family))
    line("armatures     :", [a.name for a in arms])

    for arm in arms:
        line("-" * 60)
        line("armature:", arm.name)
        chain, p = [], arm
        while p is not None:
            chain.append(f"{p.name}({p.type})")
            p = p.parent
        line("  parent chain:", " <- ".join(chain))
        line("  location", tuple(round(v, 4) for v in arm.location),
             " scale", tuple(round(v, 4) for v in arm.scale))

        ad = arm.animation_data
        act = ad.action if ad else None
        strips = [s.action.name for t in (ad.nla_tracks if ad else ())
                  for s in t.strips if s.action]
        line("  active action:", act.name if act else None)
        line("  NLA strips   :", len(strips), strips[:3])
        if act is None and not strips:
            continue

        target = act
        if target is None:
            for t in ad.nla_tracks:
                for s in t.strips:
                    if s.action:
                        target = s.action
                        break
                if target:
                    break
        first, last = (int(round(v)) for v in target.frame_range)
        line("  measured on  :", target.name, f"frames {first}..{last}")

        sc = bpy.context.scene
        tops = [b for b in arm.data.bones if b.parent is None]
        cands = tops + [c for b in tops for c in b.children]
        line(f"  root bones: {[b.name for b in tops]}")
        rows = []
        for b in cands:
            pose = arm.pose.bones.get(b.name)
            if pose is None:
                continue
            sc.frame_set(first)
            a = (arm.matrix_world @ pose.matrix).translation.copy()
            sc.frame_set(last)
            c = (arm.matrix_world @ pose.matrix).translation.copy()
            rows.append(((c - a).length, b.name, len(b.children_recursive)))
        for dist, name, kids in sorted(rows, reverse=True):
            line(f"    {name:<24} travel {dist:.4f}  descendants {kids}")

        # What else moves furthest, in case the carrier sits deeper.
        deep = []
        for pose in arm.pose.bones:
            sc.frame_set(first)
            a = (arm.matrix_world @ pose.matrix).translation.copy()
            sc.frame_set(last)
            c = (arm.matrix_world @ pose.matrix).translation.copy()
            deep.append(((c - a).length, pose.name,
                         pose.bone.parent.name if pose.bone.parent else None))
        deep.sort(reverse=True)
        line("  furthest travel in the whole rig:")
        for dist, name, parent in deep[:5]:
            line(f"    {name:<24} {dist:.4f}   parent {parent}")

    # Objects that are NOT under the armature stay behind when it gets countered.
    if arms:
        loose = [o.name for o in family
                 if o is not arms[0] and arms[0] not in (o.parent, getattr(o.parent, "parent", None))]
        line("-" * 60)
        line("not under", arms[0].name, ":", loose[:8], "..." if len(loose) > 8 else "")
line("done")
