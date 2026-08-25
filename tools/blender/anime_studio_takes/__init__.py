"""Switch a whole AnimeStudio take at once.

An FBX with several AnimationClips gives Blender one action per clip *per data-block*:
one for the armature, and one more for every shape-key data-block. Blender links only the
first of each -- see io_scene_fbx/import_fbx.py, blen_read_animations():
"Only the first found action is linked to objects, more complex setups are not handled,
it's up to user to reproduce them!"

So after switching the armature to clip B the face keeps playing clip A. This add-on
reproduces that setup: it groups the actions by take and assigns a whole take in one
click. It only ever reassigns actions and slots -- no keyframe or shape-key value is
touched.

Actions are found in three ways, so an unusual import still works: what the data-block
already carries, the "<data-block>|<take>" names Blender's FBX importer writes, and the
data paths of the curves themselves. Both the one-action-per-data-block layout and the
one-action-per-take-with-several-slots layout of Blender 4.4+ are handled.
"""

import re

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty

bl_info = {
    "name": "AnimeStudio Takes",
    "author": "AnimeStudio Ultimate",
    "version": (1, 2, 0),
    "blender": (4, 3, 0),
    "location": "View3D > Sidebar (N) > AnimeStudio",
    "description": "Switch armature and all shape-key actions of an imported FBX take together",
    "category": "Animation",
}

# Blender appends the AnimLayer name to the action name when it differs from the stack
# name. AnimeStudio always writes "Base Layer", and Blender <= 4.5 caps names at 63
# characters, which can cut that suffix off mid-word, so match any prefix of it.
_LAYER_SUFFIX = re.compile(r"\|B(?:a(?:s(?:e(?: (?:L(?:a(?:y(?:e(?:r)?)?)?)?)?)?)?)?)?$")

# Only paths naming a sub-element identify a data-block -- key_blocks["Fac_Jaw"].value or
# pose.bones["Bip001"].location. A bare "location" resolves on every object.
_SPECIFIC = '["'

_RNA_TO_ID_TYPE = {"Object": 'OBJECT', "Key": 'KEY', "Material": 'MATERIAL'}

# Dynamic enum items must stay referenced or Blender frees the strings mid-draw.
_enum_cache = []


# --------------------------------------------------------------------------------------
# actions, slots and channel bags across Blender versions


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


def _slot_curves(action, slot):
    """The f-curves that belong to one slot of a slotted action."""
    for layer in getattr(action, "layers", ()):
        for strip in layer.strips:
            bag = None
            try:
                bag = strip.channelbag(slot)
            except (TypeError, AttributeError):
                handle = getattr(slot, "handle", None)
                for candidate in getattr(strip, "channelbags", ()):
                    if getattr(candidate, "slot_handle", None) == handle:
                        bag = candidate
                        break
            if bag is not None:
                return bag.fcurves
    return ()


def _id_type_of(data):
    return getattr(data, "id_type", None) or _RNA_TO_ID_TYPE.get(data.bl_rna.identifier)


def _channel_groups(action):
    """[(slot_or_None, path)] -- one telling data path per slot of an action.

    A slotted action can drive an armature and several shape-key data-blocks at once, so
    each slot is looked at separately; a Blender 4.3 action has no slots and yields one.
    """
    slots = getattr(action, "slots", None)
    groups = []
    if slots:
        for slot in slots:
            for curve in _slot_curves(action, slot):
                if _SPECIFIC in curve.data_path:
                    groups.append((slot, curve.data_path))
                    break
    else:
        for curve in _curves(action):
            if _SPECIFIC in curve.data_path:
                groups.append((None, curve.data_path))
                break
    return groups


def _pick_slot(action, data, adt=None):
    """The slot of `action` that drives `data`, or None if the action has none.

    Never falls back to "the first slot": an OBJECT slot assigned to a shape-key
    data-block raises "This slot is not suitable for this data-block type".
    """
    slots = getattr(action, "slots", None)
    if not slots:
        return None

    if adt is not None and hasattr(adt, "action_suitable_slots"):
        # Blender's own answer, once the action is assigned. Already type-filtered.
        suitable = list(adt.action_suitable_slots)
    else:
        want = _id_type_of(data)
        suitable = [s for s in slots if getattr(s, "target_id_type", None) == want]

    if len(suitable) == 1:
        return suitable[0]
    if not suitable:
        return None
    # Several slots of the right type: the importer names them after the data-block.
    for slot in suitable:
        if getattr(slot, "name_display", None) == data.name:
            return slot
    # Otherwise let the curves decide which one addresses this data-block.
    for slot in suitable:
        for curve in _slot_curves(action, slot):
            try:
                data.path_resolve(curve.data_path)
            except (ValueError, AttributeError, TypeError):
                break
            return slot
    return None


def _assign(data, action):
    """Assigns action (and on Blender 4.4+ a fitting slot). False if no slot fits."""
    if not data.animation_data:
        data.animation_data_create()
    adt = data.animation_data

    if not getattr(action, "slots", None):
        try:
            adt.action = action                   # Blender 4.3 and earlier
        except (RuntimeError, TypeError):
            return False        # Blender refuses an action whose paths do not fit
        return True

    previous, previous_slot = adt.action, getattr(adt, "action_slot", None)
    try:
        adt.action = action
    except (RuntimeError, TypeError):
        return False
    slot = _pick_slot(action, data, adt)
    if slot is None:
        adt.action = previous
        if previous is not None and previous_slot is not None:
            try:
                adt.action_slot = previous_slot
            except (RuntimeError, TypeError):
                pass
        return False
    adt.action_slot = slot
    return True


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


def _ownership():
    """Which data-block each action in the file belongs to, as far as that is knowable.

    Two characters imported side by side use the same channel and bone names, so the
    structural fallback needs to know that an action is already spoken for.
    """
    owned = {}
    by_name = {o.name: o for o in bpy.data.objects}
    by_name.update({k.name: k for k in bpy.data.shape_keys})
    unnamed = False
    for action in bpy.data.actions:
        owner, sep, _ = action.name.partition("|")
        data = by_name.get(owner) if sep else None
        if data is not None:
            owned[action] = data
        else:
            unnamed = True
    if unnamed:
        # Only worth walking every data-block when some action's name says nothing.
        for data in by_name.values():
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
        self._pool = None
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
            owner, sep, _rest = action.name.partition("|")
            if sep:
                by_owner.setdefault(owner, []).append(action)

        for data in blocks:
            found = {}
            for action in by_owner.get(data.name, ()):
                found.setdefault(_take_of(action.name, data.name), action)
            self._add_linked(data, found)
            if found:
                self.per_id[data] = found

        # And what the curves themselves address, for everything the names missed.
        for data in blocks:
            self._merge(data, self._by_data_path(data))

        self.actions_matched = len({a for f in self.per_id.values() for a in f.values()})
        if not self.per_id:
            for action in bpy.data.actions:
                self.example = action.name
                break
        else:
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

    def _channels(self):
        """[(action, slot_or_None, path)] for every channel group in the file."""
        if self._pool is None:
            self._pool = [(action, slot, path)
                          for action in bpy.data.actions
                          for slot, path in _channel_groups(action)]
        return self._pool

    def _allowed(self, action, data):
        # A multi-slot action drives several data-blocks by design, so ownership says
        # nothing about it. A single-slot one belongs to whoever already holds it.
        if len(getattr(action, "slots", ()) or ()) > 1:
            return True
        owner = self._owned.get(action)
        return owner is None or owner == data

    def _by_data_path(self, data):
        """Actions whose curves address something that exists on this data-block."""
        want = _id_type_of(data)
        found = {}
        for action, slot, path in self._channels():
            # Cheap type filter first -- path_resolve on a mismatch costs an exception,
            # and a character can have hundreds of data-blocks.
            known = getattr(slot, "target_id_type", None) if slot is not None \
                else ('KEY' if path.startswith("key_blocks[") else None)
            if known is not None and known != want:
                continue
            if not self._allowed(action, data):
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

    Returns (assigned, guessed, missing, unslotted): how many data-blocks were switched,
    which ones needed the frame range to identify the take, which ones have no action for
    it, and which ones have one but no slot that fits them.
    """
    scan = Scan(obj or bpy.context.object)
    if isinstance(take, int):
        take = scan.takes[take]

    assigned, missing, guessed, unslotted = 0, [], [], []
    for data in scan.per_id:
        action, was_guess = scan.action_for(data, take)
        if action is None:
            missing.append(data.name)
            continue
        if not _assign(data, action):
            unslotted.append(data.name)
            continue
        assigned += 1
        if was_guess:
            guessed.append(data.name)
    return assigned, guessed, missing, unslotted


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
            slot = _pick_slot(action, data)
            if slot is None and getattr(action, "slots", None):
                continue                       # no slot fits this data-block
            track = adt.nla_tracks.new()
            track.name = take
            strip = track.strips.new(take, int(action.frame_range[0]), action)
            if slot is not None and hasattr(strip, "action_slot"):
                strip.action_slot = slot
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

        assigned, guessed, missing, unslotted = set_take(scan.takes[index], context.object)
        msg = f"{scan.takes[index]}: {assigned} data-block(s)"
        if guessed:
            msg += f"  (name truncated, matched by frame range: {_few(guessed)})"
        if missing:
            msg += f"  (no action for: {_few(missing)})"
        if unslotted:
            msg += f"  (no matching action slot for: {_few(unslotted)})"
        self.report({'WARNING' if unslotted else 'INFO'}, msg)
        return {'FINISHED'}


def _few(names, limit=4):
    head = ", ".join(names[:limit])
    return head + ", ..." if len(names) > limit else head


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
