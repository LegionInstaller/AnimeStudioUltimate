using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.Migoto
{
    /// <summary>
    /// What an export with every variant as its own mesh has to write down so the variants
    /// can be switched again: which mesh shows under which values of the mod's variables,
    /// and which image a material wears under them.
    ///
    /// An FBX has no place for any of that. It goes into "&lt;fbx name&gt;.variants.json"
    /// next to the model, and the Blender add-on turns it into one row of buttons per
    /// variable -- the mod's own switches, instead of sixty meshes to hide by hand.
    ///
    /// A condition the reader could not make sense of is left out, which makes it hold
    /// always: the same choice the export itself makes.
    /// </summary>
    public sealed class VariantSheet
    {
        public const string Extension = ".variants.json";

        private static readonly string[] Slots = { "Diffuse", "NormalMap" };

        private readonly object gate = new object();

        // Keyed by the mesh's path and the material's name, so converting a model twice --
        // a preview, then the export -- leaves one entry.
        private readonly Dictionary<string, List<List<IniCondition>>> meshes =
            new Dictionary<string, List<List<IniCondition>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<MigotoTexture>> materials =
            new Dictionary<string, IReadOnlyList<MigotoTexture>>(StringComparer.Ordinal);

        /// <summary>A variant's mesh, and the cases it is drawn in -- any one is enough.</summary>
        public void AddMesh(string path, IEnumerable<List<IniCondition>> cases)
        {
            if (string.IsNullOrEmpty(path))
                return;
            lock (gate)
                meshes[path] = cases.ToList();
        }

        /// <summary>A material made from the mod's images, and everything the mod binds there.</summary>
        public void AddMaterial(string name, IReadOnlyList<MigotoTexture> bindings)
        {
            lock (gate)
                materials[name] = bindings;
        }

        /// <summary>
        /// Writes the sheet for one exported model. Only what that model holds goes in; with
        /// nothing to switch no file is written and the answer is null.
        ///
        /// Every image a switch can bring up is written next to the model as well. The
        /// export itself only writes the ones a material starts out with.
        /// </summary>
        public string Write(string fbxPath, MigotoMod mod, IReadOnlyDictionary<string, string> chosen,
                            IEnumerable<string> meshPaths, IEnumerable<string> materialNames,
                            List<string> warnings)
        {
            var folder = Path.GetDirectoryName(fbxPath) ?? "";
            var used = new List<string>();
            var meshList = new JArray();
            var materialList = new JArray();

            lock (gate)
            {
                foreach (var path in meshPaths.Where(p => p != null).Distinct())
                {
                    if (!meshes.TryGetValue(path, out var cases))
                        continue;
                    meshList.Add(new JObject
                    {
                        ["name"] = path.Substring(path.LastIndexOf('/') + 1),
                        ["when_any"] = new JArray(cases.Select(c => Terms(c, used))),
                    });
                }

                foreach (var name in materialNames.Where(n => n != null).Distinct())
                {
                    if (!materials.TryGetValue(name, out var bindings))
                        continue;
                    var slots = new JObject();
                    foreach (var slot in Slots)
                    {
                        var offered = bindings
                            .Where(b => string.Equals(b.Slot, slot, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        // One image, whatever the values: nothing to switch.
                        if (offered.Select(b => b.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                            continue;
                        var list = new JArray();
                        foreach (var binding in offered)
                        {
                            var file = Image(binding.File, folder, warnings);
                            if (file != null)
                                list.Add(new JObject
                                {
                                    ["file"] = file,
                                    ["when"] = Terms(binding.Conditions, used),
                                });
                        }
                        slots[slot] = list;
                    }
                    if (slots.Count > 0)
                        materialList.Add(new JObject { ["name"] = name, ["slots"] = slots });
                }
            }

            if (meshList.Count == 0 && materialList.Count == 0)
                return null;

            // The variables in the order the mod's own list has them, each with the value
            // the export was made for -- that is what the switches start on.
            var variables = new JArray();
            foreach (var name in mod.Variables.Keys.Concat(used).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!used.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                var values = mod.Variables.TryGetValue(name, out var known) && known.Count > 0
                    ? known : new List<string> { "0", "1" };
                string value = null;
                if (chosen == null || !chosen.TryGetValue(name, out value))
                    mod.Defaults.TryGetValue(name, out value);
                variables.Add(new JObject
                {
                    ["name"] = name,
                    ["values"] = new JArray(values),
                    ["value"] = value ?? values[0],
                });
            }

            var sheet = new JObject
            {
                ["version"] = 1,
                ["mod"] = mod.Name,
                ["variables"] = variables,
                ["meshes"] = meshList,
                ["materials"] = materialList,
            };
            var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(fbxPath) + Extension);
            File.WriteAllText(target, sheet.ToString(Formatting.Indented));
            return target;
        }

        /// <summary>
        /// The terms that have to hold together, as the sheet writes them. The variables
        /// they ask about are noted in <paramref name="used"/>.
        /// </summary>
        private static JArray Terms(IEnumerable<IniCondition> conditions, List<string> used)
        {
            var list = new JArray();
            foreach (var c in conditions)
            {
                if (c.AnyOf != null)
                {
                    list.Add(new JObject
                    {
                        ["any"] = new JArray(c.AnyOf.Select(side => Terms(side, used))),
                        ["negate"] = c.Negate,
                    });
                    continue;
                }
                // Unread, or the mod's on-switch, which an export is always made with.
                if (c.Opaque || c.Variable == null
                    || string.Equals(c.Variable, "$active", StringComparison.OrdinalIgnoreCase))
                    continue;
                used.Add(c.Variable);
                list.Add(new JObject
                {
                    ["var"] = c.Variable,
                    ["value"] = c.Value,
                    ["negate"] = c.Negate,
                });
            }
            return list;
        }

        /// <summary>The image's name as a png next to the model, written there if it is not yet.</summary>
        private static string Image(string source, string folder, List<string> warnings)
        {
            var name = Path.GetFileNameWithoutExtension(source) + ".png";
            var target = Path.Combine(folder, name);
            if (File.Exists(target))
                return name;
            var texture = ModTextures.Load(source, warnings);
            if (texture == null)
                return null;
            File.WriteAllBytes(target, texture.Data);
            return name;
        }
    }
}
