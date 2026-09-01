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

ZZZ writes one take as up to three clips -- the body, the face, and one per outfit, each
driven by its own layer of the AnimatorController. The list still shows every clip on its
own; where such layers are found, "Combine Layered Clips" appears and plays the ticked
ones together from stacked NLA tracks, which is the only way to run several actions on one
data-block at once.
"""

import re

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty

bl_info = {
    "name": "AnimeStudio Takes",
    "author": "AnimeStudio Ultimate",
    "version": (1, 4, 0),
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

# NLA tracks this add-on made carry this prefix, so a second click replaces them instead
# of stacking a duplicate on top.
_NLA_MARK = "AS| "

# Dynamic enum items must stay referenced or Blender frees the strings mid-draw.
_enum_cache = []
_layer_cache = []


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
        # A slot Blender has not typed yet fits everything -- it takes its type from the
        # first data-block it is bound to. Actions built by a script rather than by the
        # FBX importer arrive that way, and refusing them would drop them silently.
        untyped = [s for s in slots if getattr(s, "target_id_type", None) == 'UNSPECIFIED']
        return untyped[0] if len(untyped) == 1 else None
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


def _common_segments(takes):
    """How many leading "_"-separated segments every take of one character shares."""
    parts = [t.split("_") for t in takes]
    shortest = min(len(p) for p in parts) - 1      # always leave one segment behind
    n = 0
    while n < shortest and all(p[n] == parts[0][n] for p in parts):
        n += 1
    return n


def _variant_map(takes, frame_range_of):
    """Groups takes that are the same clip exported once per animated layer.

    ZZZ writes one take as up to three clips -- the body, the face, and one per outfit.
    They differ by a single word sitting right behind the character name:

        Avatar_..._Zhenzhen_Ani_Death            body
        Avatar_..._Zhenzhen_Face_Ani_Death       face
        Avatar_..._Zhenzhen_Default_Ani_Death    outfit

    Everything in front of that word is common to every take of the character, so the
    word is found by dropping the first segment behind the common prefix and looking for
    another take that matches. Nothing is hardcoded, and the frame ranges have to agree.

    That is deliberately strict enough to leave real takes alone: dropping the first
    segment of "..._Ani_Attack_Normal_01_End" or "..._Ani_Attack_Normal_P2_01" does not
    land on another take, so neither is folded into anything.

    Returns (bases, variants): the takes to offer, and base -> [(word, take)].
    """
    if len(takes) < 2:
        return list(takes), {}

    n = _common_segments(takes)
    tails = {t: t.split("_")[n:] for t in takes}
    by_tail = {"_".join(segs): t for t, segs in tails.items()}

    variants, folded = {}, set()
    for take, segs in tails.items():
        if len(segs) < 2:
            continue
        base = by_tail.get("_".join(segs[1:]))
        if base is None or base == take:
            continue
        mine, theirs = frame_range_of(take), frame_range_of(base)
        if mine is None or theirs is None:
            continue
        if abs(mine[0] - theirs[0]) > 1e-3 or abs(mine[1] - theirs[1]) > 1e-3:
            continue
        variants.setdefault(base, []).append((segs[0], take))
        folded.add(take)

    for group in variants.values():
        group.sort()
    return [t for t in takes if t not in folded], variants


class Scan:
    """Everything the panel and the operators need about one character."""

    def __init__(self, obj):
        self.root = None
        self.objects = []
        self.armatures = []
        self.shape_keys = []
        self.per_id = {}        # data-block -> {take: action}
        self.takes = []
        self.bases = []         # the body clip of every group, variants left out
        self.variants = {}      # base take -> [(word, take)], e.g. ("Face", "..._Face_Ani_X")
        self._base = {}         # variant take -> its base
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
            return

        # Every data-block contributes takes. A shape-key data-block that is animated in
        # only one clip must not cut the list down to that clip.
        #
        # Blender <= 4.5 caps names at 63 characters, so one take can arrive under two
        # spellings: the full one from a short data-block name, a cut-off one from a long
        # one. Whether a shorter name is such a cut-off, or a take of its own, is decided
        # by the data rather than by counting characters -- if any single data-block lists
        # both spellings they must be two takes ("Walk" next to "Walk_Start"), and if none
        # does, the short one is the long one truncated.
        known = [set(found) for found in self.per_id.values()]
        all_takes = set().union(*known)
        takes = []
        for take in sorted(all_takes, key=lambda t: (-len(t), t)):
            if any(longer.startswith(take)
                   and not any(take in one and longer in one for one in known)
                   for longer in takes):
                continue
            takes.append(take)
        self.takes = sorted(takes)
        for take in self.takes:
            for found in self.per_id.values():
                if take in found:
                    self.reference[take] = found[take]
                    break

        self.bases, self.variants = _variant_map(
            self.takes,
            lambda t: self.reference[t].frame_range if t in self.reference else None)
        self._base = {t: base for base, group in self.variants.items()
                      for _, t in group}

    def base_of(self, take):
        """The body clip of the group `take` belongs to -- `take` itself if it is one."""
        return self._base.get(take, take)

    def group_of(self, take, words=None):
        """The clips that play together: the body plus the variants named in `words`.

        `words` of None means every variant that was found. Works from any member of the
        group, so picking the face clip in the list and pressing Combine does the same as
        picking the body clip.
        """
        base = self.base_of(take)
        found = self.variants.get(base, ())
        return [base] + [t for word, t in found if words is None or word in words]

    def layer_words(self, take):
        """The variant words of the group `take` belongs to, e.g. ["Default", "Face"]."""
        return [word for word, _ in self.variants.get(self.base_of(take), ())]

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
    for i, take in enumerate(scan.bases):
        covered = sum(1 for data in scan.per_id if scan.action_for(data, take)[0] is not None)
        print(f"  [{i}] {take}   ({covered}/{len(scan.per_id)} data-blocks)")
        for word, variant in scan.variants.get(take, ()):
            print(f"        + {word:<12} {variant}")
    return scan.bases


def set_take(take, obj=None):
    """Assigns one take to every animated data-block of one character.

    Returns (assigned, guessed, missing, unslotted): how many data-blocks were switched,
    which ones needed the frame range to identify the take, which ones this take does not
    animate, and which ones have an action but no slot that fits them.

    A data-block the take does not animate has its action cleared, so the face cannot keep
    playing the previous clip while the body moves to this one.
    """
    scan = Scan(obj or bpy.context.object)
    if isinstance(take, int):
        take = scan.takes[take]

    assigned, missing, guessed, unslotted = 0, [], [], []
    for data in scan.per_id:
        action, was_guess = scan.action_for(data, take)
        if action is None:
            if data.animation_data and data.animation_data.action:
                data.animation_data.action = None
            missing.append(data.name)
            continue
        if not _assign(data, action):
            unslotted.append(data.name)
            continue
        assigned += 1
        if was_guess:
            guessed.append(data.name)
    return assigned, guessed, missing, unslotted


def _clear_our_tracks(adt, takes):
    """Removes the NLA tracks a previous run of this add-on left behind.

    Both spellings are dropped: the plain take names that "Push All Takes" writes and the
    marked ones from the combined assignment, so the two features cannot end up
    evaluating on top of each other.
    """
    stale = [t for t in adt.nla_tracks
             if t.name.startswith(_NLA_MARK) or t.name in takes]
    for track in stale:
        adt.nla_tracks.remove(track)


def set_take_combined(take, obj=None, words=None):
    """Plays a take together with its variants -- body, face and outfit at once.

    One data-block can hold only a single active action, so the variants go on stacked NLA
    tracks instead. They animate largely disjoint channels, so the upper strip replaces
    what it drives and everything else falls through to the strip below -- which is what
    the layers of the original AnimatorController do.

    The largest action goes to the bottom: the body carries the whole skeleton, the outfit
    only its own bones, the face only eyes, teeth and its shape keys. Where two variants
    do touch the same channel the smaller, more specific one then wins.

    `words` picks which variants to include -- None takes every one that was found.

    Returns (strips, blocks, missing): strips written, data-blocks touched, and the
    data-blocks this take does not animate at all.
    """
    scan = Scan(obj or bpy.context.object)
    if isinstance(take, int):
        take = scan.bases[take]
    group = scan.group_of(take, words)

    strips, touched, missing = 0, 0, []
    for data in scan.per_id:
        if not data.animation_data:
            data.animation_data_create()
        adt = data.animation_data
        adt.action = None
        _clear_our_tracks(adt, scan.takes)

        found = []
        for take in group:
            action, _ = scan.action_for(data, take)
            if action is not None and action not in [a for _, a in found]:
                found.append((take, action))
        if not found:
            missing.append(data.name)
            continue
        found.sort(key=lambda pair: -len(_curves(pair[1])))

        wrote = False
        for take, action in found:
            slot = _pick_slot(action, data)
            if slot is None and getattr(action, "slots", None):
                continue                       # no slot of this action fits this data-block
            track = adt.nla_tracks.new()
            track.name = _NLA_MARK + take
            strip = track.strips.new(take, int(action.frame_range[0]), action)
            strip.blend_type = 'REPLACE'
            strip.extrapolation = 'HOLD'
            if slot is not None and hasattr(strip, "action_slot"):
                strip.action_slot = slot
            strips += 1
            wrote = True
        if wrote:
            touched += 1
    return strips, touched, missing


def push_all_takes_to_nla(obj=None):
    """Puts every take on its own NLA track, so all of them stay visible at once."""
    scan = Scan(obj or bpy.context.object)
    pushed = 0
    for data in scan.per_id:
        if not data.animation_data:
            data.animation_data_create()
        adt = data.animation_data
        adt.action = None
        _clear_our_tracks(adt, scan.takes)
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
    """Every clip, one entry each. Layered clips are marked, not folded away."""
    _enum_cache.clear()
    scan = Scan(context.object)
    for i, take in enumerate(scan.takes):
        words = scan.layer_words(take)
        note = "plays with " + ", ".join(words) if words else ""
        _enum_cache.append((take, f"{take}  (+{len(words)})" if words else take, note, i))
    if not _enum_cache:
        _enum_cache.append(('NONE', "no takes found", "", 0))
    return _enum_cache


def _layer_items(self, context):
    """The variant words of the selected take, as toggles.

    An ENUM_FLAG needs its values to be powers of two and stable while the buttons are on
    screen, so they are handed out by position in the -- already sorted -- word list.
    """
    _layer_cache.clear()
    take = getattr(context.scene, "anime_studio_take", "")
    for i, word in enumerate(Scan(context.object).layer_words(take)):
        _layer_cache.append((word, word, f"Include the {word} clip", 1 << i))
    return _layer_cache


def _select_all_layers(scene, context):
    """Every layer of the newly picked take starts switched on."""
    words = Scan(context.object).layer_words(scene.anime_studio_take)
    try:
        scene.anime_studio_layers = set(words)
    except (TypeError, ValueError):
        pass


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
        take = scan.takes[index]

        assigned, guessed, missing, unslotted = set_take(take, context.object)
        msg = f"{take}: {assigned} data-block(s)"
        if guessed:
            msg += f"  (name truncated, matched by frame range: {_few(guessed)})"
        if missing:
            msg += f"  (not animated in this take, cleared: {_few(missing)})"
        if unslotted:
            msg += f"  (no matching action slot for: {_few(unslotted)})"
        self.report({'WARNING' if unslotted else 'INFO'}, msg)
        return {'FINISHED'}


def _few(names, limit=4):
    head = ", ".join(names[:limit])
    return head + ", ..." if len(names) > limit else head


class ANIMESTUDIO_OT_combine(bpy.types.Operator):
    bl_idname = "anime_studio.combine"
    bl_label = "Combine Layered Clips"
    bl_description = ("Play the selected clip together with the ticked layers -- body, "
                      "face and outfit -- from stacked NLA tracks")
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return context.object is not None

    def execute(self, context):
        scan = Scan(context.object)
        take = context.scene.anime_studio_take
        if take not in scan.takes:
            self.report({'WARNING'}, "No AnimeStudio takes found on this character")
            return {'CANCELLED'}

        words = set(context.scene.anime_studio_layers)
        available = scan.layer_words(take)
        if not available:
            self.report({'WARNING'}, f"{take} has no layered clips to combine")
            return {'CANCELLED'}

        strips, blocks, missing = set_take_combined(take, context.object, words)
        base = scan.base_of(take)
        chosen = ", ".join(w for w in available if w in words) or "nothing extra"
        msg = f"{base} + {chosen}: {strips} strip(s) on {blocks} data-block(s)"
        if missing:
            msg += f"  (not animated in this take: {_few(missing)})"
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

        # Only offered where the export really split the take across layers, so a character
        # animated in one piece never sees this at all.
        words = scan.layer_words(context.scene.anime_studio_take)
        if words:
            box = layout.box()
            box.label(text="Layered clips for this take", icon='NLA')
            box.label(text="body  (always included)")
            box.prop(context.scene, "anime_studio_layers", expand=True)
            box.operator(ANIMESTUDIO_OT_combine.bl_idname, icon='NLA_PUSHDOWN')

        layout.separator()
        layout.operator(ANIMESTUDIO_OT_push_nla.bl_idname, icon='NLA')

        layout.prop(context.scene, "anime_studio_show_details")
        if context.scene.anime_studio_show_details:
            box = layout.box()
            for line in diagnose(scan):
                box.label(text=line)
            box.operator(ANIMESTUDIO_OT_report.bl_idname, icon='CONSOLE')


_classes = (ANIMESTUDIO_OT_apply_take, ANIMESTUDIO_OT_combine, ANIMESTUDIO_OT_push_nla,
            ANIMESTUDIO_OT_report, ANIMESTUDIO_PT_takes)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.anime_studio_take = EnumProperty(
        name="Take",
        description="AnimationClip to put on the whole character",
        items=_take_items,
        update=_select_all_layers,
    )
    bpy.types.Scene.anime_studio_layers = EnumProperty(
        name="Layers",
        description="Which of the layered clips of this take to play along with the body",
        items=_layer_items,
        options={'ENUM_FLAG'},
    )
    bpy.types.Scene.anime_studio_show_details = BoolProperty(
        name="Show details",
        description="Show what the scan found on this character",
        default=False,
    )


def unregister():
    del bpy.types.Scene.anime_studio_show_details
    del bpy.types.Scene.anime_studio_layers
    del bpy.types.Scene.anime_studio_take
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    _enum_cache.clear()
    _layer_cache.clear()


if __name__ == "__main__":
    register()
