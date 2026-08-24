"""Switch a whole AnimeStudio take at once.

An FBX with several AnimationClips gives Blender one action per clip *per data-block*:
one for the armature, and one more for every shape-key data-block. Blender links only the
first of each -- see io_scene_fbx/import_fbx.py, blen_read_animations():
"Only the first found action is linked to objects, more complex setups are not handled,
it's up to user to reproduce them!"

So after switching the armature to clip B the face keeps playing clip A. This add-on
reproduces that setup: it groups the imported actions by take and assigns a whole take in
one click. It only ever reassigns actions -- no keyframe or shape-key value is touched.
"""

import re

import bpy
from bpy.props import EnumProperty, IntProperty

bl_info = {
    "name": "AnimeStudio Takes",
    "author": "AnimeStudio Ultimate",
    "version": (1, 0, 0),
    "blender": (4, 3, 0),
    "location": "View3D > Sidebar (N) > AnimeStudio",
    "description": "Switch armature and all shape-key actions of an imported FBX take together",
    "category": "Animation",
}

# Blender appends the AnimLayer name to the action name when it differs from the stack
# name. AnimeStudio always writes "Base Layer", and on Blender <= 4.5 the 63-character
# name limit can cut that suffix off mid-word, so match any prefix of it.
_LAYER_SUFFIX = re.compile(r"\|B(?:a(?:s(?:e(?: (?:L(?:a(?:y(?:e(?:r)?)?)?)?)?)?)?)?)?$")

# Dynamic enum items must stay referenced or Blender frees the strings mid-draw.
_enum_cache = []


# --------------------------------------------------------------------------------------
# take discovery


def _root_of(obj):
    while obj.parent is not None:
        obj = obj.parent
    return obj


def _character(obj):
    """The one imported character `obj` belongs to: its root object and everything under it."""
    if obj is None:
        return None, []
    root = _root_of(obj)
    objects = [root] + list(root.children_recursive)
    data = list(objects)
    for ob in objects:
        if ob.type == 'MESH' and ob.data.shape_keys is not None:
            data.append(ob.data.shape_keys)
    return root, data


def _take_of(action_name, owner_name):
    rest = action_name
    if rest.startswith(owner_name + "|"):
        rest = rest[len(owner_name) + 1:]
    else:
        # The owner name itself was truncated; fall back to the first separator.
        cut = rest.find("|")
        rest = rest[cut + 1:] if cut >= 0 else rest
    return _LAYER_SUFFIX.sub("", rest)


def collect(obj):
    """Returns (root, {data-block: {take: action}}, [take names], {take: reference action})."""
    root, blocks = _character(obj)
    per_id = {}
    for data in blocks:
        found = {}
        prefix = data.name + "|"
        for action in bpy.data.actions:
            if action.name.startswith(prefix):
                found[_take_of(action.name, data.name)] = action
        if found:
            per_id[data] = found
    if not per_id:
        return root, {}, [], {}
    # The data-block with the shortest name loses the fewest characters to truncation,
    # so its take names are the most complete ones available.
    reference = min(per_id, key=lambda d: len(d.name))
    return root, per_id, sorted(per_id[reference]), per_id[reference]


def _resolve(found, take, reference=None):
    """Finds the action for `take` in one data-block, allowing for a truncated name.

    Returns (action, was_guess). On Blender <= 4.5 two long clip names can truncate to the
    same 63-character action name; the frame range then tells them apart, because every
    data-block of one take covers exactly the same range.
    """
    action = found.get(take)
    if action is not None:
        return action, False
    candidates = [k for k in found if take.startswith(k) or k.startswith(take)]
    if len(candidates) == 1:
        return found[candidates[0]], False
    if len(candidates) > 1 and reference is not None:
        want = reference.frame_range
        matched = [k for k in candidates
                   if abs(found[k].frame_range[0] - want[0]) < 1e-3
                   and abs(found[k].frame_range[1] - want[1]) < 1e-3]
        if len(matched) == 1:
            return found[matched[0]], True
    return None, False


def _assign(data, action):
    if not data.animation_data:
        data.animation_data_create()
    data.animation_data.action = action
    # Blender 4.4+ needs the slot as well, or the action drives nothing.
    slots = getattr(action, "slots", None)
    if slots and hasattr(data.animation_data, "action_slot"):
        data.animation_data.action_slot = slots[0]


# --------------------------------------------------------------------------------------
# console API


def list_takes(obj=None):
    """Prints the takes of the character `obj` belongs to (default: the active object)."""
    obj = obj or bpy.context.object
    root, per_id, takes, ref = collect(obj)
    print(f"{root.name if root else '-'}: {len(takes)} take(s), "
          f"{len(per_id)} animated data-block(s)")
    for i, take in enumerate(takes):
        covered = sum(1 for found in per_id.values()
                      if _resolve(found, take, ref.get(take))[0] is not None)
        print(f"  [{i}] {take}   ({covered}/{len(per_id)} data-blocks)")
    return takes


def set_take(take, obj=None):
    """Assigns one take to every animated data-block of one character.

    Returns (assigned, guessed, missing).
    """
    obj = obj or bpy.context.object
    _root, per_id, takes, ref = collect(obj)
    if isinstance(take, int):
        take = takes[take]

    assigned, missing, guessed = 0, [], []
    for data, found in per_id.items():
        action, was_guess = _resolve(found, take, ref.get(take))
        if action is None:
            missing.append(data.name)
            continue
        _assign(data, action)
        assigned += 1
        if was_guess:
            guessed.append(data.name)
    return assigned, guessed, missing


def push_all_takes_to_nla(obj=None):
    """Puts every take on its own NLA track, so all of them stay visible at once."""
    obj = obj or bpy.context.object
    _root, per_id, takes, ref = collect(obj)
    pushed = 0
    for data, found in per_id.items():
        if not data.animation_data:
            data.animation_data_create()
        adt = data.animation_data
        adt.action = None
        # Drop only the tracks a previous run of this add-on made, so repeated clicks do
        # not stack duplicates.
        for track in [t for t in adt.nla_tracks if t.name in takes]:
            adt.nla_tracks.remove(track)
        for take in takes:
            action, _ = _resolve(found, take, ref.get(take))
            if action is None:
                continue
            track = adt.nla_tracks.new()
            track.name = take
            strip = track.strips.new(take, int(action.frame_range[0]), action)
            slots = getattr(action, "slots", None)
            if slots and hasattr(strip, "action_slot"):
                strip.action_slot = slots[0]
            pushed += 1
    return pushed, len(per_id)


# --------------------------------------------------------------------------------------
# UI


def _take_items(self, context):
    _enum_cache.clear()
    _root, _per_id, takes, _ref = collect(context.object)
    for i, take in enumerate(takes):
        _enum_cache.append((take, take, "", i))
    if not _enum_cache:
        _enum_cache.append(('NONE', "no takes found", "", 0))
    return _enum_cache


class ANIMESTUDIO_OT_apply_take(bpy.types.Operator):
    bl_idname = "anime_studio.apply_take"
    bl_label = "Apply Take"
    bl_description = "Assign the selected take to the armature and to every shape-key data-block"
    bl_options = {'REGISTER', 'UNDO'}

    step: IntProperty(default=0, options={'SKIP_SAVE'})

    @classmethod
    def poll(cls, context):
        return context.object is not None

    def execute(self, context):
        _root, _per_id, takes, _ref = collect(context.object)
        if not takes:
            self.report({'WARNING'}, "No AnimeStudio takes found on this character")
            return {'CANCELLED'}

        current = context.scene.anime_studio_take
        index = takes.index(current) if current in takes else 0
        if self.step:
            index = (index + self.step) % len(takes)
            context.scene.anime_studio_take = takes[index]

        assigned, guessed, missing = set_take(takes[index], context.object)
        msg = f"{takes[index]}: {assigned} data-block(s)"
        if guessed:
            msg += f"  (name truncated, matched by frame range: {', '.join(guessed)})"
        if missing:
            msg += f"  (no action for: {', '.join(missing)})"
        self.report({'INFO'}, msg)
        return {'FINISHED'}


class ANIMESTUDIO_OT_push_nla(bpy.types.Operator):
    bl_idname = "anime_studio.push_nla"
    bl_label = "Push All Takes to NLA"
    bl_description = ("Put every take on its own NLA track, on the armature and on every "
                      "shape-key data-block")
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return context.object is not None

    def execute(self, context):
        pushed, blocks = push_all_takes_to_nla(context.object)
        if not pushed:
            self.report({'WARNING'}, "No AnimeStudio takes found on this character")
            return {'CANCELLED'}
        self.report({'INFO'}, f"{pushed} strip(s) across {blocks} data-block(s)")
        return {'FINISHED'}


class ANIMESTUDIO_PT_takes(bpy.types.Panel):
    bl_label = "Takes"
    bl_idname = "ANIMESTUDIO_PT_takes"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "AnimeStudio"

    def draw(self, context):
        layout = self.layout
        obj = context.object
        if obj is None:
            layout.label(text="Select a part of the character")
            return

        root, per_id, takes, _ref = collect(obj)
        layout.label(text=f"Character: {root.name}", icon='OUTLINER_OB_ARMATURE')
        if not takes:
            layout.label(text="No takes found", icon='INFO')
            return
        layout.label(text=f"{len(takes)} take(s), {len(per_id)} data-block(s)")

        layout.prop(context.scene, "anime_studio_take", text="")

        row = layout.row(align=True)
        row.operator(ANIMESTUDIO_OT_apply_take.bl_idname,
                     text="Previous", icon='TRIA_LEFT').step = -1
        row.operator(ANIMESTUDIO_OT_apply_take.bl_idname,
                     text="Next", icon='TRIA_RIGHT').step = 1
        layout.operator(ANIMESTUDIO_OT_apply_take.bl_idname, icon='CHECKMARK').step = 0

        layout.separator()
        layout.operator(ANIMESTUDIO_OT_push_nla.bl_idname, icon='NLA')


_classes = (ANIMESTUDIO_OT_apply_take, ANIMESTUDIO_OT_push_nla, ANIMESTUDIO_PT_takes)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.anime_studio_take = EnumProperty(
        name="Take",
        description="AnimationClip to put on the whole character",
        items=_take_items,
    )


def unregister():
    del bpy.types.Scene.anime_studio_take
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    _enum_cache.clear()


if __name__ == "__main__":
    register()
