"""Switch a whole AnimeStudio take at once.

An FBX with several AnimationClips gives Blender one action per clip *per data-block*:
one for the armature, and one more for every shape-key data-block. Blender links only the
first of each -- see io_scene_fbx/import_fbx.py, blen_read_animations():
"Only the first found action is linked to objects, more complex setups are not handled,
it's up to user to reproduce them!"

So after switching the armature to clip B the face keeps playing clip A. This add-on
reproduces that setup: it groups the actions by take and assigns a whole take in one
click. It only ever reassigns actions -- no keyframe or shape-key value is touched.

Actions are found in three ways, so an unusual import still works:
its assigned/NLA actions, the "<data-block>|<take>" names Blender's FBX importer writes,
and -- as a fallback -- the data paths of the curves themselves.
"""

import re

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty

bl_info = {
    "name": "AnimeStudio Takes",
    "author": "AnimeStudio Ultimate",
    "version": (1, 1, 0),
    "blender": (4, 3, 0),
    "location": "View3D > Sidebar (N) > AnimeStudio",
    "description": "Switch armature and all shape-key actions of an imported FBX take together",
    "category": "Animation",
}

# Blender appends the AnimLayer name to the action name when it differs from the stack
# name. AnimeStudio always writes "Base Layer", and Blender <= 4.5 caps names at 63
# characters, which can cut that suffix off mid-word, so match any prefix of it.
_LAYER_SUFFIX = re.compile(r"\|B(?:a(?:s(?:e(?: (?:L(?:a(?:y(?:e(?:r)?)?)?)?)?)?)?)?)?$")

# Dynamic enum items must stay referenced or Blender frees the strings mid-draw.
_enum_cache = []


# --------------------------------------------------------------------------------------
# small compatibility helpers


def _curves(action):
    """The f-curves of an action, on both the legacy and the slotted Action API."""
    curves = getattr(action, "fcurves", None)
    if curves is not None:
        return curves
    out = []
    for layer in action.layers:
        for strip in layer.strips:
            for bag in getattr(strip, "channelbags", ()):
                out.extend(bag.fcurves)
    return out


def _first_path(action):
    for curve in _curves(action):
        return curve.data_path
    return None


def _assign(data, action):
    if not data.animation_data:
        data.animation_data_create()
    data.animation_data.action = action
    # Blender 4.4+ needs the slot as well, or the action drives nothing.
    slots = getattr(action, "slots", None)
    if slots and hasattr(data.animation_data, "action_slot"):
        data.animation_data.action_slot = slots[0]


# --------------------------------------------------------------------------------------
# what belongs to one character


def _root_of(obj):
    while obj.parent is not None:
        obj = obj.parent
    return obj


def _subtree(obj):
    return [obj] + list(obj.children_recursive)


def _character_objects(obj):
    """Every object of the one character `obj` belongs to.

    Normally that is the root object plus its descendants. Meshes are pulled in through
    their Armature modifier as well, so a rig whose meshes were unparented -- or a mesh
    picked while its armature sits in another hierarchy -- still resolves to one character.
    """
    objects = dict.fromkeys(_subtree(_root_of(obj)))
    armatures = {o for o in objects if o.type == 'ARMATURE'}

    # The active object is a mesh that lives outside its rig.
    if not armatures:
        for mod in getattr(obj, "modifiers", ()):
            if mod.type == 'ARMATURE' and mod.object is not None:
                armatures.add(mod.object)
                objects.update(dict.fromkeys(_subtree(_root_of(mod.object))))

    # Meshes deformed by one of those armatures but parented somewhere else.
    for other in bpy.data.objects:
        if other in objects or other.type != 'MESH':
            continue
        for mod in other.modifiers:
            if mod.type == 'ARMATURE' and mod.object in armatures:
                objects[other] = None
                break

    return list(objects)


class Scan:
    """Everything the panel and the operators need about one character."""

    def __init__(self, obj):
        self.root = None
        self.objects = []
        self.armatures = []
        self.shape_keys = []
        self.per_id = {}        # data-block -> {take: action}
        self.takes = []
        self.reference = {}     # take -> action of the least-truncated data-block
        self.actions_total = len(bpy.data.actions)
        self.actions_matched = 0
        self.example = None     # an unmatched action name, for the diagnosis
        self._pools = None
        self._owned = {}
        if obj is None:
            return

        self.root = _root_of(obj)
        self.objects = _character_objects(obj)
        self.armatures = [o for o in self.objects if o.type == 'ARMATURE']
        self.shape_keys = [o.data.shape_keys for o in self.objects
                           if o.type == 'MESH' and o.data.shape_keys is not None]
        self._collect()

    # -- action discovery ---------------------------------------------------------------

    def _collect(self):
        blocks = list(self.objects) + self.shape_keys
        self._owned = _ownership()

        # Blender's FBX importer names actions "<data-block>|<take>[|<layer>]".
        by_owner = {}
        for action in bpy.data.actions:
            owner, sep, rest = action.name.partition("|")
            if sep:
                by_owner.setdefault(owner, []).append(action)

        for data in blocks:
            found = {}
            for action in by_owner.get(data.name, ()):
                found.setdefault(_take_of(action.name, data.name), action)
            self._add_linked(data, found)
            if found:
                self.per_id[data] = found

        # Shape keys are the point of this add-on, so they always get the structural
        # pass too -- a face whose second take is not encoded in the action name would
        # otherwise stay stuck on the one action Blender happened to link.
        for key in self.shape_keys:
            self._merge(key, self._by_data_path(key))

        if not self.per_id:
            # Nothing matched at all: ask every data-block what its curves address.
            for data in blocks:
                self._merge(data, self._by_data_path(data))
        else:
            # Some data-blocks know fewer takes than others -- an armature whose actions
            # were renamed, for instance. Let those ask as well.
            best = max(len(found) for found in self.per_id.values())
            for data in blocks:
                if len(self.per_id.get(data, ())) < best:
                    self._merge(data, self._by_data_path(data))

        self.actions_matched = len({a for f in self.per_id.values() for a in f.values()})
        if not self.per_id:
            for action in bpy.data.actions:
                self.example = action.name
                break

        if self.per_id:
            # The data-block with the shortest name loses the fewest characters to
            # truncation, so its take names are the most complete ones available.
            reference = min(self.per_id, key=lambda d: len(d.name))
            self.takes = sorted(self.per_id[reference])
            self.reference = self.per_id[reference]

    def _merge(self, data, found):
        if not found:
            return
        target = self.per_id.setdefault(data, {})
        for take, action in found.items():
            target.setdefault(take, action)

    def _add_linked(self, data, found):
        """Actions this data-block already carries -- assigned or on an NLA strip."""
        adt = data.animation_data
        if not adt:
            return
        actions = [adt.action] if adt.action else []
        for track in adt.nla_tracks:
            actions.extend(strip.action for strip in track.strips if strip.action)
        for action in actions:
            found.setdefault(_take_of(action.name, data.name), action)

    def _pool(self, shape_keys):
        """Actions worth testing, split by whether they drive shape keys.

        Only paths naming a sub-element are usable -- key_blocks["Fac_Jaw"].value or
        pose.bones["Bip001"].location -- because a bare "location" resolves on every
        object and would match anything.
        """
        if self._pools is None:
            keyed, other = [], []
            for action in bpy.data.actions:
                path = _first_path(action)
                if not path or '["' not in path:
                    continue
                (keyed if path.startswith("key_blocks[") else other).append((action, path))
            self._pools = (keyed, other)
        return self._pools[0 if shape_keys else 1]

    def _by_data_path(self, data):
        """Actions whose curves address something that exists on this data-block."""
        found = {}
        for action, path in self._pool(isinstance(data, bpy.types.Key)):
            # Another character's action can address the same channel or bone names,
            # so never take one that already belongs to a different data-block.
            owner = self._owned.get(action)
            if owner is not None and owner != data:
                continue
            try:
                data.path_resolve(path)
            except (ValueError, AttributeError, TypeError):
                continue
            found.setdefault(_take_of(action.name, data.name), action)
        return found

    # -- take lookup --------------------------------------------------------------------

    def action_for(self, data, take):
        """Finds the action for `take` on one data-block. Returns (action, was_guess).

        On Blender <= 4.5 two long clip names can truncate to the same 63-character
        action name, and an unusual import may not encode the take in the name at all.
        The frame range then tells the takes apart, because every data-block of one take
        covers exactly the same range.
        """
        found = self.per_id.get(data)
        if not found:
            return None, False
        action = found.get(take)
        if action is not None:
            return action, False

        candidates = [k for k in found if take.startswith(k) or k.startswith(take)]
        reference = self.reference.get(take)
        if len(candidates) == 1:
            return found[candidates[0]], False
        if reference is None:
            return None, False
        if not candidates:
            candidates = list(found)
        want = reference.frame_range
        matched = [k for k in candidates
                   if abs(found[k].frame_range[0] - want[0]) < 1e-3
                   and abs(found[k].frame_range[1] - want[1]) < 1e-3]
        if len(matched) == 1:
            return found[matched[0]], True
        return None, False


def _ownership():
    """Which data-block each action in the file belongs to, as far as that is knowable.

    Two characters imported side by side use the same channel and bone names, so the
    structural fallback needs to know that an action is already spoken for.
    """
    owned = {}
    for action in bpy.data.actions:
        owner, sep, _ = action.name.partition("|")
        if not sep:
            continue
        data = bpy.data.objects.get(owner) or bpy.data.shape_keys.get(owner)
        if data is not None:
            owned[action] = data
    for data in list(bpy.data.objects) + list(bpy.data.shape_keys):
        adt = data.animation_data
        if not adt:
            continue
        if adt.action:
            owned.setdefault(adt.action, data)
        for track in adt.nla_tracks:
            for strip in track.strips:
                if strip.action:
                    owned.setdefault(strip.action, data)
    return owned


def _take_of(action_name, owner_name):
    rest = action_name
    if rest.startswith(owner_name + "|"):
        rest = rest[len(owner_name) + 1:]
    else:
        # Another data-block's name in front, e.g. an action found through its curves.
        # An action named after the clip alone keeps its whole name.
        owner, sep, tail = rest.partition("|")
        if sep and (owner in bpy.data.objects or owner in bpy.data.shape_keys):
            rest = tail
    stripped = _LAYER_SUFFIX.sub("", rest)
    return stripped or rest or action_name


# --------------------------------------------------------------------------------------
# console API


def list_takes(obj=None):
    """Prints the takes of the character `obj` belongs to (default: the active object)."""
    scan = Scan(obj or bpy.context.object)
    print(f"{scan.root.name if scan.root else '-'}: {len(scan.takes)} take(s), "
          f"{len(scan.per_id)} animated data-block(s)")
    for line in diagnose(scan):
        print("  " + line)
    for i, take in enumerate(scan.takes):
        covered = sum(1 for data in scan.per_id if scan.action_for(data, take)[0] is not None)
        print(f"  [{i}] {take}   ({covered}/{len(scan.per_id)} data-blocks)")
    return scan.takes


def set_take(take, obj=None):
    """Assigns one take to every animated data-block of one character.

    Returns (assigned, guessed, missing).
    """
    scan = Scan(obj or bpy.context.object)
    if isinstance(take, int):
        take = scan.takes[take]

    assigned, missing, guessed = 0, [], []
    for data in scan.per_id:
        action, was_guess = scan.action_for(data, take)
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
    scan = Scan(obj or bpy.context.object)
    pushed = 0
    for data in scan.per_id:
        if not data.animation_data:
            data.animation_data_create()
        adt = data.animation_data
        adt.action = None
        # Drop only the tracks a previous run of this add-on made, so repeated clicks do
        # not stack duplicates.
        for track in [t for t in adt.nla_tracks if t.name in scan.takes]:
            adt.nla_tracks.remove(track)
        for take in scan.takes:
            action, _ = scan.action_for(data, take)
            if action is None:
                continue
            track = adt.nla_tracks.new()
            track.name = take
            strip = track.strips.new(take, int(action.frame_range[0]), action)
            slots = getattr(action, "slots", None)
            if slots and hasattr(strip, "action_slot"):
                strip.action_slot = slots[0]
            pushed += 1
    return pushed, len(scan.per_id)


def diagnose(scan):
    """Why a scan found what it found -- shown in the panel when no takes turned up."""
    lines = [
        f"Objects in character: {len(scan.objects)}",
        f"Armatures found: {len(scan.armatures)}",
        f"Shape Key datablocks found: {len(scan.shape_keys)}",
        f"Actions in file: {scan.actions_total}",
        f"AnimeStudio actions found: {scan.actions_matched}",
    ]
    if not scan.per_id and scan.example:
        lines.append(f"Unmatched, e.g.: {scan.example}")
    return lines


# --------------------------------------------------------------------------------------
# UI


def _take_items(self, context):
    _enum_cache.clear()
    for i, take in enumerate(Scan(context.object).takes):
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
        scan = Scan(context.object)
        if not scan.takes:
            self.report({'WARNING'}, "No AnimeStudio takes found on this character")
            return {'CANCELLED'}

        current = context.scene.anime_studio_take
        index = scan.takes.index(current) if current in scan.takes else 0
        if self.step:
            index = (index + self.step) % len(scan.takes)
            context.scene.anime_studio_take = scan.takes[index]

        assigned, guessed, missing = set_take(scan.takes[index], context.object)
        msg = f"{scan.takes[index]}: {assigned} data-block(s)"
        if guessed:
            msg += f"  (name truncated, matched by frame range: {', '.join(guessed)})"
        if missing:
            msg += f"  (no action for: {', '.join(missing[:4])}"
            msg += ", ...)" if len(missing) > 4 else ")"
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


class ANIMESTUDIO_OT_report(bpy.types.Operator):
    bl_idname = "anime_studio.report"
    bl_label = "Print Details to Console"
    bl_description = "Print the take list and the scan details to the system console"

    @classmethod
    def poll(cls, context):
        return context.object is not None

    def execute(self, context):
        list_takes(context.object)
        self.report({'INFO'}, "Written to the system console")
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

        scan = Scan(obj)
        layout.label(text=f"Character: {scan.root.name}", icon='OUTLINER_OB_ARMATURE')

        if not scan.takes:
            layout.label(text="No takes found", icon='ERROR')
            box = layout.box()
            for line in diagnose(scan):
                box.label(text=line)
            layout.operator(ANIMESTUDIO_OT_report.bl_idname, icon='CONSOLE')
            return

        layout.label(text=f"{len(scan.takes)} take(s), {len(scan.per_id)} data-block(s)")
        layout.prop(context.scene, "anime_studio_take", text="")

        row = layout.row(align=True)
        row.operator(ANIMESTUDIO_OT_apply_take.bl_idname,
                     text="Previous", icon='TRIA_LEFT').step = -1
        row.operator(ANIMESTUDIO_OT_apply_take.bl_idname,
                     text="Next", icon='TRIA_RIGHT').step = 1
        layout.operator(ANIMESTUDIO_OT_apply_take.bl_idname, icon='CHECKMARK').step = 0

        layout.separator()
        layout.operator(ANIMESTUDIO_OT_push_nla.bl_idname, icon='NLA')

        layout.prop(context.scene, "anime_studio_show_details")
        if context.scene.anime_studio_show_details:
            box = layout.box()
            for line in diagnose(scan):
                box.label(text=line)
            box.operator(ANIMESTUDIO_OT_report.bl_idname, icon='CONSOLE')


_classes = (ANIMESTUDIO_OT_apply_take, ANIMESTUDIO_OT_push_nla, ANIMESTUDIO_OT_report,
            ANIMESTUDIO_PT_takes)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.anime_studio_take = EnumProperty(
        name="Take",
        description="AnimationClip to put on the whole character",
        items=_take_items,
    )
    bpy.types.Scene.anime_studio_show_details = BoolProperty(
        name="Show details",
        description="Show what the scan found on this character",
        default=False,
    )


def unregister():
    del bpy.types.Scene.anime_studio_show_details
    del bpy.types.Scene.anime_studio_take
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    _enum_cache.clear()


if __name__ == "__main__":
    register()
