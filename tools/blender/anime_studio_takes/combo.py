"""Plays a combo through the AnimatorController graph AnimeStudio writes next to an FBX.

No bpy in here on purpose, so the logic can be run and checked outside Blender:

    python combo.py <name>.animator.json "PressAttackA PressAttackA PressAttackA"

A combo is a list of steps separated by spaces:

    PressAttackA            press one input (the "Trigger_" prefix can be left out)
    PressAttackA+PerfectEvade   several inputs in the same moment
    Int_BranchIndex=1       set a parameter from here on (Bool: 0 or 1)
    wait                    let the current clip play out
    wait:120                let 120 frames pass, e.g. walking while Bool_IsMoving=1

Every input is given at the earliest frame the controller accepts it, like a perfect
player. Clips that follow on their own, like an attack's _End clip or the way back to
Idle, are taken along. The times follow the game: ZZZ counts transitions in frames of the
running clip, and AnimeStudio already resolved those into frames in the file.
"""

import json

_PREFIXES = ("", "Trigger_", "Bool_", "Int_", "Float_")

# How long an input may wait for its window before the step counts as impossible, in
# frames past the end of a clip that does not loop.
_HOLD_LIMIT = 600


class ComboError(Exception):
    pass


class Segment:
    """One clip in the result: which state, from which frame, blended in over how long."""

    def __init__(self, state, start, offset, blend, cause):
        self.state = state      # state index
        self.start = start      # frame the state became active
        self.offset = offset    # frame of its own clip it starts at
        self.blend = blend      # blend-in length in frames
        self.cause = cause      # what led here, for the report
        self.end = None         # frame the next state took over (None for the last one)


class Graph:
    def __init__(self, data):
        if not str(data.get("format", "")).startswith("AnimeStudio animator graph"):
            raise ComboError("not an AnimeStudio animator graph")
        layer = data["layer"]
        self.controller = data.get("controller", "")
        self.rate = float(data.get("frameRate", 60) or 60)
        self.states = layer["states"]
        self.selectors = layer.get("selectors", [])
        self.any_state = layer.get("anyState", [])
        self.default_state = int(layer.get("defaultState", 0))
        self.params = {p["name"]: p for p in data.get("parameters", [])}

    @classmethod
    def load(cls, path):
        with open(path, encoding="utf-8") as f:
            return cls(json.load(f))

    # -- names ------------------------------------------------------------------------

    def state_index(self, name):
        for i, state in enumerate(self.states):
            if state["name"] == name:
                return i
        return None

    def start_state(self, name=""):
        """The named state, else Idle, else the controller's default."""
        for candidate in (name, "Idle"):
            if candidate:
                index = self.state_index(candidate)
                if index is not None:
                    return index
        if name:
            raise ComboError(f"no state named {name}")
        return self.default_state

    def param(self, name):
        for prefix in _PREFIXES:
            if prefix + name in self.params:
                return prefix + name
        raise ComboError(f"unknown input or parameter: {name}")

    def triggers(self):
        """The trigger parameters some base-layer condition really tests."""
        used = set()
        transitions = list(self.any_state)
        for state in self.states:
            transitions.extend(state.get("transitions", ()))
        for selector in self.selectors:
            transitions.extend(selector.get("transitions", ()))
        for t in transitions:
            for c in t.get("conditions", ()):
                used.add(c["param"])
        return [n for n, p in self.params.items() if p.get("type") == "Trigger" and n in used]

    def parse(self, text):
        """[(kind, {param: value}, {trigger, ...})], see the module docstring."""
        steps = []
        for token in text.split():
            if token.lower() == "wait":
                steps.append(("wait", {}, set()))
                continue
            if token.lower().startswith("wait:"):
                try:
                    frames = int(token[5:])
                except ValueError:
                    raise ComboError(f"{token}: give the frames as a whole number, e.g. wait:120")
                steps.append(("frames", {"": max(0, frames)}, set()))
                continue
            values, triggers = {}, set()
            for part in token.split("+"):
                if not part:
                    continue
                if "=" in part:
                    key, raw = part.split("=", 1)
                    name = self.param(key)
                    values[name] = self._value(name, raw)
                else:
                    name = self.param(part)
                    if self.params[name].get("type") != "Trigger":
                        raise ComboError(f"{part} is not an input, give it a value: {part}=1")
                    triggers.add(name)
            steps.append(("input" if triggers else "set", values, triggers))
        return steps

    def _value(self, name, raw):
        kind = self.params[name].get("type")
        try:
            number = float(raw)
        except ValueError:
            raise ComboError(f"{name}={raw} is not a number")
        if kind in ("Bool", "Trigger"):
            return number != 0
        if kind == "Int":
            return int(number)
        return number


class _Run:
    """The state of one playthrough: where we are, what is set, what happened."""

    def __init__(self, graph, start_state):
        self.g = graph
        self.values = {}
        for name, p in graph.params.items():
            default = p.get("default")
            self.values[name] = default if default is not None else 0
        self.pending = set()                # triggers set and not yet used by a transition
        self.state = start_state
        self.entered = 0                    # global frame at which local frame 0 would be
        self.local = 0                      # current frame of the state's own clip
        self.segments = [Segment(start_state, 0, 0, 0, "start")]

    # -- the clock the conditions see ------------------------------------------------------

    def frames(self, state=None):
        return int(self.g.states[self.state if state is None else state].get("frames") or 0)

    def frame_count(self):
        frames, state = self.frames(), self.g.states[self.state]
        if frames and state.get("loop"):
            return self.local % frames
        return min(self.local, frames) if frames else self.local

    def holds(self, c):
        name, mode, v = c["param"], c["mode"], c.get("value", 0)
        if name == "FrameCount":
            x = self.frame_count()
        elif name == "NormalizedTime":
            frames = self.frames()
            x = self.frame_count() / frames if frames else 0.0
        elif self.g.params.get(name, {}).get("type") == "Trigger":
            x = name in self.pending
        else:
            x = self.values.get(name, 0)
        if mode == "If":
            return bool(x)
        if mode == "IfNot":
            return not bool(x)
        if mode == "Greater":
            return x > v
        if mode == "GreaterEqual":
            return x >= v
        if mode == "Less":
            return x < v
        if mode == "Equals":
            return x == v
        if mode == "NotEqual":
            return x != v
        if mode == "ExitTime":
            frames = self.frames()
            return frames > 0 and self.local / frames >= v
        return False        # a mode we do not know never passes

    def used_triggers(self, conditions):
        return {c["param"] for c in conditions
                if c["mode"] == "If" and self.g.params.get(c["param"], {}).get("type") == "Trigger"}

    # -- transitions -------------------------------------------------------------------------

    def resolve(self, target):
        """Follows selectors to a state. (state, triggers the path tested) or None."""
        used, seen = set(), set()
        while "selector" in target:
            index = target["selector"]
            if index in seen or index >= len(self.g.selectors):
                return None
            seen.add(index)
            for t in self.g.selectors[index].get("transitions", ()):
                if all(self.holds(c) for c in t.get("conditions", ())):
                    used |= self.used_triggers(t.get("conditions", ()))
                    target = t["to"]
                    break
            else:
                return None
        if "state" not in target:
            return None
        return target["state"], used

    def exit_frame(self, t):
        if "exitFrame" in t:
            return t["exitFrame"]
        if "exitNormalized" in t:
            return round(t["exitNormalized"] * self.frames())
        return None

    def ready(self):
        """The transition that fires on this frame: (transition, state, triggers) or None.

        AnyState transitions come first and each list is taken in order, the first one
        whose conditions hold wins. Those are the rules the game's Animator follows.
        """
        candidates = [t for t in self.g.any_state
                      if t.get("canTransitionToSelf", True) or t["to"].get("state") != self.state]
        candidates += self.g.states[self.state].get("transitions", ())
        for t in candidates:
            exit_frame = self.exit_frame(t)
            if exit_frame is not None and self.local < exit_frame:
                continue
            if not all(self.holds(c) for c in t.get("conditions", ())):
                continue
            resolved = self.resolve(t["to"])
            if resolved is None:
                continue
            state, used = resolved
            return t, state, used | self.used_triggers(t.get("conditions", ()))
        return None

    def take(self, t, state, used, cause):
        now = self.entered + self.local
        if "blendFrames" in t:
            blend = t["blendFrames"]
        else:
            blend = round(t.get("blendNormalized", 0) * self.frames())
        if "offsetFrames" in t:
            offset = t["offsetFrames"]
        else:
            offset = round(t.get("offsetNormalized", 0) * self.frames(state))
        self.pending -= used
        self.segments[-1].end = now
        self.segments.append(Segment(state, now, offset, blend, cause))
        self.state = state
        self.entered = now - offset
        # A state's own transitions are looked at from its next frame on, as in the game.
        self.local = offset + 1

    def limit(self):
        """The last local frame worth waiting for in the current state."""
        frames, state = self.frames(), self.g.states[self.state]
        if state.get("loop"):
            return self.local + max(frames, 1)
        return max(frames, self.local) + _HOLD_LIMIT

    # -- steps ---------------------------------------------------------------------------------

    def press(self, triggers, label):
        """Runs until one of `triggers` is used. Notes what played out on the way."""
        self.pending |= triggers
        notes, guard = [], 0
        limit = self.limit()
        began = self.g.states[self.state]["name"]
        while True:
            fired = self.ready()
            if fired is not None:
                t, state, used = fired
                if used & triggers:
                    self.take(t, state, used, label)
                    return notes
                name = self.g.states[state]["name"]
                self.take(t, state, used, "follows on its own")
                notes.append(f"{label}: {self.g.states[self.segments[-2].state]['name']} "
                             f"ran out first, the input lands in {name}")
                limit = self.limit()
                guard += 1
                if guard > 200:
                    break
                continue
            self.local += 1
            if self.local > limit:
                break
        ended = self.g.states[self.state]["name"]
        self.pending -= triggers
        if ended == began:
            raise ComboError(f"{label} is not possible from {began}")
        raise ComboError(f"{label} is not possible from {began} or what follows it (up to {ended})")

    def advance(self, frames):
        """Lets `frames` frames pass, taking whatever transitions fire on their own."""
        end = self.entered + self.local + frames
        guard = 0
        while self.entered + self.local < end and guard < 10000:
            fired = self.ready()
            if fired is not None:
                t, state, used = fired
                self.take(t, state, used, "follows on its own")
                guard += 1
                continue
            self.local += 1

    def settle(self):
        """Lets automatic transitions run until a state just keeps playing.

        A state that comes round again would repeat for good, like a walk cycle while
        Bool_IsMoving is still on, so the run stops there instead and says so.
        """
        seen = {self.state}
        limit = self.limit()
        while True:
            fired = self.ready()
            if fired is not None:
                t, state, used = fired
                if state in seen:
                    name = self.g.states[state]["name"]
                    return [f"{name} would repeat on its own, so the combo ends there. "
                            f"Set the value that keeps it going back to stop it, e.g. Bool_IsMoving=0"]
                seen.add(state)
                self.take(t, state, used, "follows on its own")
                limit = self.limit()
                continue
            self.local += 1
            if self.local > limit:
                return []


def play(graph, text, start="", lead_in=0, settle=True):
    """Plays the combo `text`. Returns (segments, notes).

    `lead_in` frames of the start state play before the first input; `settle` lets the
    last clip run out back to a resting state, as the game would.
    """
    steps = graph.parse(text)
    run = _Run(graph, graph.start_state(start))
    run.local = max(0, int(lead_in))
    notes = []
    for kind, values, triggers in steps:
        if kind == "frames":
            run.advance(values[""])
            continue
        run.values.update(values)
        if kind == "wait":
            notes += run.settle()
        elif kind == "input":
            label = "+".join(sorted(n.replace("Trigger_", "") for n in triggers))
            notes += run.press(triggers, label)
    if settle:
        notes += run.settle()
    return run.segments, notes


def describe(graph, segments):
    lines = []
    for seg in segments:
        state = graph.states[seg.state]
        extra = f", blend {seg.blend}" if seg.blend else ""
        extra += f", from its frame {seg.offset}" if seg.offset else ""
        lines.append(f"{seg.start:6d}  {state['name']:<28} {seg.cause}{extra}")
    return lines


if __name__ == "__main__":
    import sys
    g = Graph.load(sys.argv[1])
    try:
        segs, notes = play(g, sys.argv[2] if len(sys.argv) > 2 else "",
                           start=sys.argv[3] if len(sys.argv) > 3 else "")
    except ComboError as e:
        sys.exit(f"error: {e}")
    print("\n".join(describe(g, segs)))
    for note in notes:
        print("note:", note)
