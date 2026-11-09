"""Reads the .variants.json AnimeStudio writes next to an FBX that was exported with every
variant of a 3DMigoto mod as its own mesh, and tells what shows for a set of values.

No bpy in here on purpose, so the logic can be run and checked outside Blender:

    python variants.py <name>.variants.json van=1 th=2

The file lists the mod's variables with their values, and for every variant mesh and
every switchable image the conditions the mod's ini puts on it. A condition is a list of
terms that all have to hold:

    {"var": "$th", "value": "2", "negate": false}       $th == 2
    {"any": [[terms], [terms]], "negate": false}        one of the sides holds

A mesh shows when one of its "when_any" cases holds. Of the images listed for a material
slot the last one whose condition holds is used, as in the ini, where a later line
overrides an earlier one.
"""

import json
import re

EXTENSION = ".variants.json"

# Blender numbers a second data-block of the same name: Body.001
_COPY = re.compile(r"\.\d{3}$")

# Blender before 5.0 cuts names at this many characters.
_OLD_NAME_LENGTH = 63


class Sheet:
    def __init__(self, data):
        if data.get("version") != 1:
            raise ValueError("written by a newer AnimeStudio, update the add-on")
        self.mod = data.get("mod", "")
        # [(name, [values], value to start with)], in the order of the mod's ini
        self.variables = [(v["name"], [str(x) for x in v["values"]], str(v["value"]))
                          for v in data.get("variables", [])]
        self.meshes = {m["name"]: m["when_any"] for m in data.get("meshes", [])}
        self.materials = {m["name"]: m["slots"] for m in data.get("materials", [])}

    @classmethod
    def load(cls, path):
        with open(path, encoding="utf-8") as f:
            return cls(json.load(f))

    def defaults(self):
        return {name: value for name, _, value in self.variables}

    def mesh(self, name):
        """The mesh of the file that the Blender object `name` is, or None."""
        return _lookup(name, self.meshes)

    def material(self, name):
        """The material of the file that the Blender material `name` is, or None."""
        return _lookup(name, self.materials)

    def shows(self, mesh, values):
        return any(_all(case, values) for case in self.meshes[mesh])

    def images(self, material, values):
        """{slot: (file to use or None, every file of the slot)} for one material."""
        picked = {}
        for slot, bindings in self.materials[material].items():
            file = None
            for binding in bindings:
                if _all(binding["when"], values):
                    file = binding["file"]
            picked[slot] = (file, [b["file"] for b in bindings])
        return picked


def _all(terms, values):
    return all(_holds(term, values) for term in terms)


def _holds(term, values):
    """Values compare as text without case, and a variable nobody set counts as 0."""
    if "any" in term:
        hit = any(_all(side, values) for side in term["any"])
    else:
        value = str(values.get(term["var"].lower(), "0"))
        hit = value.lower() == str(term["value"]).lower()
    return hit != bool(term.get("negate"))


def _lookup(name, table):
    if name in table:
        return name
    plain = _COPY.sub("", name)
    if plain in table:
        return plain
    for key in table:
        if len(key) > _OLD_NAME_LENGTH and key[:_OLD_NAME_LENGTH] in (name, plain):
            return key
    return None


def keyed(values):
    """Values as the conditions look them up: by the variable's name in lower case."""
    return {name.lower(): str(value) for name, value in values.items()}


if __name__ == "__main__":
    import sys

    sheet = Sheet.load(sys.argv[1])
    values = sheet.defaults()
    for pair in sys.argv[2:]:
        name, _, value = pair.partition("=")
        values["$" + name.lstrip("$")] = value
    print("  ".join(f"{k}={v}" for k, v in values.items()))
    lookup = keyed(values)
    for mesh in sheet.meshes:
        if sheet.shows(mesh, lookup):
            print("mesh ", mesh)
    for material in sheet.materials:
        for slot, (file, _) in sheet.images(material, lookup).items():
            print("image", material, slot, file)
