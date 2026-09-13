using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.Migoto
{
    /// <summary>One <c>drawindexed</c> line: a slice of the index buffer.</summary>
    public sealed class MigotoDraw
    {
        public int IndexCount { get; set; }
        public int StartIndex { get; set; }
        public int BaseVertex { get; set; }

        /// <summary>What the enclosing <c>if</c> demanded. Empty when the draw always runs.</summary>
        public List<IniCondition> Conditions { get; set; } = new List<IniCondition>();

        public bool IsOptional => Conditions.Count > 0;

        /// <summary>Whether this draw runs, given the values chosen for the mod's variables.</summary>
        public bool Applies(IReadOnlyDictionary<string, string> chosen) =>
            Conditions.All(c => c.Holds(chosen));

        public override string ToString() =>
            $"{IndexCount} indices from {StartIndex}"
            + (Conditions.Count == 0 ? ""
               : "  (only when " + string.Join(" and ", Conditions.Select(c => c.ToString())) + ")");
    }

    /// <summary>
    /// One image the ini binds to a slot, and what had to be true for it. A mod may offer
    /// several for the same slot -- a tattoo variant, a glossier light map -- so which one
    /// counts is only known once the variables are chosen.
    /// </summary>
    public sealed class MigotoTexture
    {
        public string Slot { get; set; }
        public string File { get; set; }
        public List<IniCondition> Conditions { get; set; } = new List<IniCondition>();

        public bool Applies(IReadOnlyDictionary<string, string> chosen) =>
            chosen == null || Conditions.All(c => c.Holds(chosen));

        /// <summary>
        /// The image for a slot under the chosen variables, or null. The last binding wins:
        /// the ini sets state in order, so a later assignment overwrites an earlier one.
        /// </summary>
        public static string Pick(IEnumerable<MigotoTexture> list, string slot,
                                  IReadOnlyDictionary<string, string> chosen)
        {
            string found = null;
            foreach (var t in list)
            {
                if (string.Equals(t.Slot, slot, StringComparison.OrdinalIgnoreCase)
                    && t.Applies(chosen))
                    found = t.File;
            }
            return found;
        }

        public override string ToString() => $"{Slot} = {System.IO.Path.GetFileName(File)}";
    }

    /// <summary>An index buffer of one part, with the draws that use it.</summary>
    public sealed class MigotoObject
    {
        public string Name { get; set; }
        public string IndexFile { get; set; }
        public string IndexFormat { get; set; }
        public int MatchFirstIndex { get; set; }
        public List<MigotoDraw> Draws { get; } = new List<MigotoDraw>();

        /// <summary>
        /// The mod's own images for this object, in the order the ini binds them. These are
        /// the point of most mods -- the replaced geometry is UV-mapped for them, not for the
        /// game's texture.
        /// </summary>
        public List<MigotoTexture> Textures { get; } = new List<MigotoTexture>();

        public string Texture(string slot, IReadOnlyDictionary<string, string> chosen) =>
            MigotoTexture.Pick(Textures, slot, chosen);

        public override string ToString() => $"{Name}: {Draws.Count} draw(s)";
    }

    /// <summary>
    /// An override that binds images but draws nothing: the mod changes what a piece looks
    /// like, not its shape.
    ///
    /// Faces are done this way, and for a good reason -- a replaced face mesh loses its blend
    /// shapes, because Migoto only ever sees the finished buffer. Swapping the texture keeps
    /// the mimicry intact.
    /// </summary>
    public sealed class MigotoTextureSet
    {
        public string Name { get; set; }
        public List<MigotoTexture> Textures { get; } = new List<MigotoTexture>();

        public string Texture(string slot, IReadOnlyDictionary<string, string> chosen) =>
            MigotoTexture.Pick(Textures, slot, chosen);

        /// <summary>What it binds, in order -- the identity of the set for telling two apart.</summary>
        public string Signature =>
            string.Join("|", Textures.Select(t => t.Slot + "=" + t.File));

        public override string ToString() => $"{Name}: {Textures.Count} image(s)";
    }

    /// <summary>
    /// One replaceable piece of a character -- its three vertex streams plus the index
    /// buffers drawn from them. Named after the stem of its files ("RemielleBody"), because
    /// that is the only name the mod agrees on: the ini identifies the game's buffers by
    /// runtime hashes, which do not exist anywhere in the shipped assets.
    /// </summary>
    public sealed class MigotoPart
    {
        public string Name { get; set; }
        public int VertexCount { get; set; }

        public string PositionFile { get; set; }
        public string BlendFile { get; set; }
        public string TexcoordFile { get; set; }
        public int PositionStride { get; set; }
        public int BlendStride { get; set; }
        public int TexcoordStride { get; set; }

        public List<MigotoObject> Objects { get; } = new List<MigotoObject>();

        /// <summary>False when the ini never draws this part -- leftover files from an
        /// earlier version of the mod, which are common in hand-edited folders.</summary>
        public bool IsDrawn => Objects.Any(o => o.Draws.Count > 0);

        private MigotoVertex[] vertices;

        public MigotoVertex[] Vertices(List<string> warnings)
        {
            return vertices ??= VertexBuffers.Read(
                PositionFile, PositionStride, BlendFile, BlendStride,
                TexcoordFile, TexcoordStride, VertexCount, warnings);
        }

        /// <summary>
        /// The highest bone index the part actually weights, and how many distinct ones it
        /// uses. Both are what pairs a part with a renderer: the indices address that
        /// renderer's <c>m_Bones</c> directly, so a part only fits a renderer with at least
        /// <c>HighestBone + 1</c> bones.
        /// </summary>
        public (int Highest, int Distinct) BoneRange(List<string> warnings)
        {
            var vertices = Vertices(warnings);
            var used = new HashSet<int>();
            var highest = -1;
            foreach (var v in vertices)
            {
                if (v.W0 > 0) { used.Add(v.B0); highest = Math.Max(highest, v.B0); }
                if (v.W1 > 0) { used.Add(v.B1); highest = Math.Max(highest, v.B1); }
                if (v.W2 > 0) { used.Add(v.B2); highest = Math.Max(highest, v.B2); }
                if (v.W3 > 0) { used.Add(v.B3); highest = Math.Max(highest, v.B3); }
            }
            while (highest > 0 && Redundant(vertices, highest))
            {
                used.Remove(highest);
                highest = used.Count == 0 ? -1 : used.Max();
            }
            return (highest, used.Count);
        }

        /// <summary>
        /// Whether a bone carries no weight anyone needs: every vertex that names it is
        /// already fully weighted without it.
        ///
        /// Mods do this. The Pulchra outfit mod puts a full extra influence on bone 184 over
        /// 23297 vertices whose other weights already sum to exactly 1 -- and the shipped
        /// <c>Pulchra_Body</c> has 184 bones, so 0..183. Counting the phantom left only the
        /// LOD avatars as candidates and hid the right target by exactly one bone.
        /// </summary>
        private static bool Redundant(MigotoVertex[] vertices, int bone)
        {
            var seen = false;
            foreach (var v in vertices)
            {
                var sum = v.W0 + v.W1 + v.W2 + v.W3;
                for (int k = 0; k < 4; k++)
                {
                    var w = k == 0 ? v.W0 : k == 1 ? v.W1 : k == 2 ? v.W2 : v.W3;
                    var b = k == 0 ? v.B0 : k == 1 ? v.B1 : k == 2 ? v.B2 : v.B3;
                    if (w <= 0 || b != bone)
                        continue;
                    seen = true;
                    if (sum - w < 0.99f)
                        return false;       // the rest of this vertex needs it
                }
            }
            return seen;
        }

        public override string ToString() =>
            $"{Name}: {VertexCount} vertices, {Objects.Count} index buffer(s)"
            + (IsDrawn ? "" : "  (not drawn)");
    }

    /// <summary>
    /// A 3DMigoto / XXMI mod folder, read into memory.
    ///
    /// The ini alone does not say which parts exist -- it says which GPU buffers to replace,
    /// keyed by hashes taken while the game ran. What it does give is the file names, and
    /// those follow the generator's scheme: every part has a
    /// <c>&lt;stem&gt;Position.buf</c> next to a <c>&lt;stem&gt;Blend.buf</c> and a
    /// <c>&lt;stem&gt;Texcoord.buf</c>. The parts are found through those, and everything
    /// else -- strides, vertex counts, draw ranges, the toggles -- comes out of the ini.
    /// </summary>
    public sealed class MigotoMod
    {
        public string Folder { get; private set; }
        public string IniPath { get; private set; }

        /// <summary>What to call the mod: its ini for a single one, the folder when several
        /// inis were read together.</summary>
        public string Name { get; private set; }
        public List<MigotoPart> Parts { get; } = new List<MigotoPart>();

        /// <summary>Overrides that only change textures -- nothing to replace, only to repaint.</summary>
        public List<MigotoTextureSet> TextureSets { get; } = new List<MigotoTextureSet>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>The variables the mod switches its variants with, and their values.</summary>
        public Dictionary<string, List<string>> Variables { get; private set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The variables that actually decide something -- those a draw call or an image
        /// binding sits behind.
        ///
        /// An ini also carries variables for the mod's own on-screen menu: mouse position,
        /// hover state, a notification timeout. One mod declared 44 and only a third of them
        /// changed anything about the model. Offering the rest would bury the real choices.
        /// </summary>
        public Dictionary<string, List<string>> DrawVariables { get; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The value each variable starts on -- the first one it offers.</summary>
        public Dictionary<string, string> Defaults { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The image files the ini binds -- everything that is not a buffer.</summary>
        public List<string> Textures { get; } = new List<string>();

        private const string PositionSuffix = "Position.buf";

        /// <summary>
        /// Reads a mod folder: every ini in it, and if it holds none, every ini one level
        /// down.
        ///
        /// A mod is not always one ini. Bigger ones ship a folder per piece -- body, face,
        /// weapon -- each with its own ini, and the folder the user points at is the parent.
        /// Taking only the first ini would have silently dropped the sword; refusing the
        /// parent because it holds no ini itself was worse still.
        ///
        /// Returns null when there is nothing to read.
        /// </summary>
        public static MigotoMod LoadFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return null;
            var inis = Directory.GetFiles(folder, "*.ini");
            if (inis.Length == 0)
                inis = Directory.GetDirectories(folder)
                                .SelectMany(d => Directory.GetFiles(d, "*.ini"))
                                .ToArray();
            if (inis.Length == 0)
                return null;
            Array.Sort(inis, StringComparer.OrdinalIgnoreCase);
            if (inis.Length == 1)
                return Load(inis[0]);

            var merged = new MigotoMod
            {
                Folder = Path.GetFullPath(folder),
                IniPath = inis[0],
                Name = Path.GetFileName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)),
            };
            foreach (var path in inis)
            {
                // Each ini is read against its own directory, so its parts keep pointing at
                // their own files and its leftover check only looks where it should.
                var label = Path.GetFileNameWithoutExtension(path);
                MigotoMod one;
                try
                {
                    one = Load(path, false);
                }
                catch (Exception ex)
                {
                    merged.Warnings.Add($"{label}.ini could not be read, {ex.Message}");
                    continue;
                }
                merged.Absorb(one, label);
            }
            merged.Parts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            merged.CheckLeftovers(merged.Folder);
            merged.Warnings.Insert(0, $"{inis.Length} ini files read: "
                + string.Join(", ", inis.Select(Path.GetFileName)));
            return merged;
        }

        /// <summary>Takes over everything one ini contributed.</summary>
        private void Absorb(MigotoMod one, string label)
        {
            foreach (var part in one.Parts)
            {
                // Two pieces of the same mod may both call a part "Body". Keeping them apart
                // matters: the dialog offers one row per part and has to name them.
                if (Parts.Any(p => string.Equals(p.Name, part.Name, StringComparison.OrdinalIgnoreCase)))
                    part.Name = label + "/" + part.Name;
                Parts.Add(part);
            }
            foreach (var set in one.TextureSets)
            {
                if (Parts.Any(p => string.Equals(p.Name, set.Name, StringComparison.OrdinalIgnoreCase)))
                    set.Name = label + "/" + set.Name;
                if (!TextureSets.Any(s => s.Signature == set.Signature))
                    TextureSets.Add(set);
            }
            foreach (var texture in one.Textures)
            {
                if (!Textures.Contains(texture))
                    Textures.Add(texture);
            }
            foreach (var kv in one.Variables)
            {
                if (!Variables.ContainsKey(kv.Key))
                    Variables[kv.Key] = kv.Value;
            }
            foreach (var kv in one.DrawVariables)
            {
                if (!DrawVariables.ContainsKey(kv.Key))
                    DrawVariables[kv.Key] = kv.Value;
            }
            foreach (var kv in one.Defaults)
            {
                if (!Defaults.ContainsKey(kv.Key))
                    Defaults[kv.Key] = kv.Value;
            }
            foreach (var warning in one.Warnings)
                Warnings.Add(label + ": " + warning);
        }

        public static MigotoMod Load(string iniPath) => Load(iniPath, true);

        private static MigotoMod Load(string iniPath, bool leftovers)
        {
            var mod = new MigotoMod
            {
                IniPath = iniPath,
                Name = Path.GetFileNameWithoutExtension(iniPath),
                Folder = Path.GetDirectoryName(Path.GetFullPath(iniPath)),
            };
            var ini = ModIni.Load(iniPath);

            // Resource sections carry the file names; their section name is what the
            // overrides refer to.
            var resources = new Dictionary<string, IniSection>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in ini.Sections)
            {
                if (section.Name.StartsWith("Resource", StringComparison.OrdinalIgnoreCase)
                    && section.Get("filename") != null)
                    resources[section.Name] = section;
            }

            mod.Variables = ini.Variables;
            foreach (var kv in ini.Variables)
                mod.Defaults[kv.Key] = kv.Value.FirstOrDefault() ?? "0";

            foreach (var resource in resources.Values)
            {
                var file = resource.Get("filename");
                if (file.EndsWith(".buf", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".ib", StringComparison.OrdinalIgnoreCase))
                    continue;
                var full = Path.Combine(mod.Folder, file);
                if (File.Exists(full) && !mod.Textures.Contains(full))
                    mod.Textures.Add(full);
            }

            mod.BuildParts(ini, resources);
            mod.AttachObjects(ini, resources);
            mod.NoteDrawVariables();
            mod.Check();
            if (leftovers)
                mod.CheckLeftovers(mod.Folder);
            return mod;
        }

        private void BuildParts(ModIni ini, Dictionary<string, IniSection> resources)
        {
            foreach (var resource in resources.Values)
            {
                var file = resource.Get("filename");
                // Only the file name identifies a part. Tidier mods sort their buffers,
                // index buffers and textures into separate subfolders, so the path in front
                // of the name differs between files that belong together.
                var name = Path.GetFileName(file);
                if (!name.EndsWith(PositionSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var stem = name.Substring(0, name.Length - PositionSuffix.Length);
                var part = new MigotoPart
                {
                    Name = stem,
                    PositionFile = Path.Combine(Folder, file),
                    // Not every mod puts the stride on the resource; some only raise it on the
                    // override. Both are consulted before giving up.
                    PositionStride = Stride(resource, ini, stem),
                };

                var blend = FindByFile(resources, stem + "Blend.buf");
                var texcoord = FindByFile(resources, stem + "Texcoord.buf");
                if (blend != null)
                {
                    part.BlendFile = Path.Combine(Folder, blend.Get("filename"));
                    part.BlendStride = blend.GetInt("stride");
                }
                if (texcoord != null)
                {
                    part.TexcoordFile = Path.Combine(Folder, texcoord.Get("filename"));
                    part.TexcoordStride = texcoord.GetInt("stride");
                }

                part.VertexCount = DeclaredVertexCount(ini, stem);
                if (part.VertexCount <= 0)
                    part.VertexCount = FromFileSize(part, stem);
                Parts.Add(part);
            }
            Parts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        /// <summary>
        /// The stride of a buffer. Mods written by different tool versions put it in
        /// different places, so both are tried before the part is given up on.
        /// </summary>
        private static int Stride(IniSection resource, ModIni ini, string stem)
        {
            var stride = resource.GetInt("stride");
            if (stride > 0)
                return stride;
            foreach (var section in ini.Sections)
            {
                var raised = section.GetInt("override_byte_stride");
                if (raised > 0 && section.Name.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0)
                    return raised;
            }
            return -1;
        }

        /// <summary>
        /// The vertex count derived from the file, when the ini does not state one. Says why
        /// it failed rather than leaving the part with an impossible count.
        /// </summary>
        private int FromFileSize(MigotoPart part, string stem)
        {
            if (!File.Exists(part.PositionFile))
            {
                Warnings.Add($"{stem}: {Path.GetFileName(part.PositionFile)} is missing, "
                             + "the part is unusable");
                return -1;
            }
            if (part.PositionStride <= 0)
            {
                Warnings.Add($"{stem}: neither the ini nor the resource gives a stride for "
                             + "the position buffer, the part is unusable");
                return -1;
            }
            var size = new FileInfo(part.PositionFile).Length;
            var count = (int)(size / part.PositionStride);
            if (count <= 0)
            {
                Warnings.Add($"{stem}: {Path.GetFileName(part.PositionFile)} holds {size} "
                             + $"bytes, too little for stride {part.PositionStride}");
                return -1;
            }
            Warnings.Add($"{stem}: no override_vertex_count in the ini, took {count} from "
                         + "the file size");
            return count;
        }

        /// <summary>
        /// The vertex count the ini raises the buffer to. The section that carries it is
        /// named after the part; where several stems match, the longest wins, so a
        /// "Body" stem never steals the entry of a "BodyExtra".
        /// </summary>
        private int DeclaredVertexCount(ModIni ini, string stem)
        {
            var best = -1;
            var bestStem = -1;
            foreach (var section in ini.Sections)
            {
                var count = section.GetInt("override_vertex_count");
                if (count <= 0)
                    continue;
                var at = section.Name.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || stem.Length <= bestStem)
                    continue;
                best = count;
                bestStem = stem.Length;
            }
            return best;
        }

        /// <summary>
        /// Builds one object per index buffer, then runs the ini to find out what is drawn
        /// from each.
        ///
        /// A section is not a unit of work. One command list binds an index buffer, draws
        /// from it, binds a different one and draws again -- so a draw belongs to the last
        /// <c>ib</c> before it, not to the section it stands in. Reading it per section put a
        /// body's draws onto a leg's buffer: 208199 indices into a buffer holding 61722.
        /// </summary>
        private void AttachObjects(ModIni ini, Dictionary<string, IniSection> resources)
        {
            var objects = new Dictionary<string, MigotoObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in resources)
            {
                var file = resource.Value.Get("filename");
                if (!file.EndsWith(".ib", StringComparison.OrdinalIgnoreCase))
                    continue;

                var part = LongestMatch(Path.GetFileName(file));
                if (part == null)
                {
                    // Usually not a fault: a mod may replace only the index buffer of a piece
                    // and keep the game's vertices. There is nothing to swap in that case.
                    Warnings.Add($"index buffer {Path.GetFileName(file)} has no vertex buffers "
                                 + "of its own -- that piece cannot be replaced");
                    continue;
                }

                var obj = new MigotoObject
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    IndexFile = Path.Combine(Folder, file),
                    IndexFormat = resource.Value.Get("format"),
                };
                objects[resource.Key] = obj;
                part.Objects.Add(obj);
            }

            // The game enters the ini at an override that names a hash; everything else is
            // reached from there through "run".
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in ini.Sections)
            {
                if (!section.Name.StartsWith("TextureOverride", StringComparison.OrdinalIgnoreCase))
                    continue;
                var bound = new Bound();
                Execute(section, ini, resources, objects, new List<IniCondition>(),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), seen,
                        bound, section.GetInt("match_first_index", 0));
                NoteTextureSet(section, bound);
            }
        }

        /// <summary>The state a run carries along: what is bound at this point.</summary>
        private sealed class Bound
        {
            public MigotoObject Object;
            public List<MigotoTexture> Textures = new List<MigotoTexture>();

            /// <summary>How many draws this override reached. Zero, with images bound, means
            /// a texture-only swap.</summary>
            public int Draws;
        }

        /// <summary>
        /// Records an override that bound images and drew nothing.
        ///
        /// The same command list is often reached from several overrides -- a quality variant
        /// of the same piece -- and that is one swap, not two, so identical sets are folded
        /// together. A set without a diffuse is not offered: the other slots belong to the
        /// game's toon shader, which no importer rebuilds, and alone they would change
        /// nothing anyone could see.
        /// </summary>
        private void NoteTextureSet(IniSection section, Bound bound)
        {
            if (bound.Draws > 0 || bound.Textures.Count == 0)
                return;
            var set = new MigotoTextureSet { Name = ShortName(section.Name) };
            set.Textures.AddRange(bound.Textures);
            if (set.Texture("Diffuse", null) == null)
                return;
            if (TextureSets.Any(s => s.Signature == set.Signature))
                return;
            TextureSets.Add(set);
        }

        private static string ShortName(string section) =>
            section.StartsWith("TextureOverride", StringComparison.OrdinalIgnoreCase)
                ? section.Substring("TextureOverride".Length)
                : section;

        /// <summary>
        /// Walks one section in order, following <c>run</c> into command lists and keeping
        /// track of what is bound, the way 3DMigoto executes it.
        /// </summary>
        private void Execute(IniSection section, ModIni ini,
                             Dictionary<string, IniSection> resources,
                             Dictionary<string, MigotoObject> objects,
                             List<IniCondition> outer, HashSet<string> path,
                             HashSet<string> seen, Bound bound, int matchFirstIndex)
        {
            if (!path.Add(section.Name))
                return;                      // a command list that ends up running itself

            foreach (var entry in section.Entries)
            {
                var conditions = new List<IniCondition>(outer);
                conditions.AddRange(entry.Conditions);

                if (string.Equals(entry.Key, "ib", StringComparison.OrdinalIgnoreCase))
                {
                    objects.TryGetValue(entry.Value, out var target);
                    bound.Object = target;   // "ib = null" unbinds, and so does an unknown name
                    continue;
                }
                if (string.Equals(entry.Key, "run", StringComparison.OrdinalIgnoreCase))
                {
                    var target = ini.Section(entry.Value);
                    if (target != null)
                        Execute(target, ini, resources, objects, conditions, path, seen,
                                bound, matchFirstIndex);
                    continue;
                }
                if (Texture(entry, resources, conditions) is MigotoTexture texture)
                {
                    bound.Textures.Add(texture);
                    continue;
                }
                if (!string.Equals(entry.Key, "drawindexed", StringComparison.OrdinalIgnoreCase))
                    continue;

                var obj = bound.Object;
                if (obj == null)
                    continue;
                var draw = ParseDraw(entry.Value, obj);
                if (draw == null)
                    continue;
                draw.Conditions = conditions;
                // Counted before the duplicate check: an override that only repeats a draw
                // another one already made still drew, and is no texture-only swap.
                bound.Draws++;

                // Several overrides reach the same command list -- a low-quality variant of
                // the same mesh, say. That is the same draw, not a second one.
                var key = obj.Name + "|" + draw.IndexCount + "|" + draw.StartIndex + "|"
                          + draw.BaseVertex + "|"
                          + string.Join(",", conditions.Select(c => c.ToString()));
                if (!seen.Add(key))
                    continue;
                if (obj.MatchFirstIndex == 0)
                    obj.MatchFirstIndex = matchFirstIndex;
                obj.Draws.Add(draw);
                foreach (var t in bound.Textures)
                {
                    if (!obj.Textures.Contains(t))
                        obj.Textures.Add(t);
                }
            }
            path.Remove(section.Name);
        }

        /// <summary>
        /// One <c>drawindexed</c>. <c>auto</c> means the whole buffer; a count of zero is a
        /// placeholder the mod leaves in, and it draws nothing.
        /// </summary>
        private static MigotoDraw ParseDraw(string value, MigotoObject obj)
        {
            if (value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var whole = Available(obj);
                return whole > 0 ? new MigotoDraw { IndexCount = whole } : null;
            }
            var parts = value.Split(',');
            if (parts.Length < 2)
                return null;
            var count = Int(parts[0]);
            if (count <= 0)
                return null;
            return new MigotoDraw
            {
                IndexCount = count,
                StartIndex = Int(parts[1]),
                BaseVertex = parts.Length > 2 ? Int(parts[2]) : 0,
            };
        }

        /// <summary>How many indices an index buffer actually holds.</summary>
        private static int Available(MigotoObject obj)
        {
            if (obj.IndexFile == null || !File.Exists(obj.IndexFile))
                return 0;
            var wide = obj.IndexFormat == null
                       || obj.IndexFormat.IndexOf("R32", StringComparison.OrdinalIgnoreCase) >= 0;
            return (int)(new FileInfo(obj.IndexFile).Length / (wide ? 4 : 2));
        }

        /// <summary>
        /// An image binding, written as <c>Resource\ZZMI\Diffuse = ref ResourceBodyDiffuse</c>.
        /// The last segment of the key is the slot; the value names a resource section, and
        /// that carries the file.
        /// </summary>
        private MigotoTexture Texture(IniEntry entry, Dictionary<string, IniSection> resources,
                                      List<IniCondition> conditions)
        {
            if (entry.Value == null
                || !entry.Value.StartsWith("ref ", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!resources.TryGetValue(entry.Value.Substring(4).Trim(), out var resource))
                return null;

            var file = resource.Get("filename");
            if (file.EndsWith(".buf", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".ib", StringComparison.OrdinalIgnoreCase))
                return null;

            var slot = entry.Key.Substring(entry.Key.LastIndexOfAny(new[] { '\\', '/' }) + 1).Trim();
            var full = Path.Combine(Folder, file);
            if (slot.Length == 0 || !File.Exists(full))
                return null;
            return new MigotoTexture { Slot = slot, File = full, Conditions = conditions };
        }

        /// <summary>Picks out the variables some draw or image actually depends on.</summary>
        private void NoteDrawVariables()
        {
            void Note(IEnumerable<IniCondition> conditions)
            {
                foreach (var c in conditions)
                {
                    if (c.Variable == null || DrawVariables.ContainsKey(c.Variable))
                        continue;
                    if (Variables.TryGetValue(c.Variable, out var values))
                        DrawVariables[c.Variable] = values;
                }
            }
            foreach (var part in Parts)
                foreach (var obj in part.Objects)
                {
                    foreach (var draw in obj.Draws)
                        Note(draw.Conditions);
                    foreach (var texture in obj.Textures)
                        Note(texture.Conditions);
                }
        }

        private MigotoPart LongestMatch(string file)
        {
            MigotoPart best = null;
            foreach (var part in Parts)
            {
                if (!file.StartsWith(part.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (best == null || part.Name.Length > best.Name.Length)
                    best = part;
            }
            return best;
        }

        private void Check()
        {
            foreach (var part in Parts)
            {
                if (part.BlendFile == null)
                    Warnings.Add($"{part.Name}: no blend buffer -- the part cannot be skinned");
                if (part.TexcoordFile == null)
                    Warnings.Add($"{part.Name}: no texcoord buffer -- no UVs");
                if (!part.IsDrawn)
                    Warnings.Add($"{part.Name}: the ini never draws this part, "
                                 + "the files look like leftovers");

                foreach (var obj in part.Objects)
                {
                    if (obj.Draws.Count == 0 || !File.Exists(obj.IndexFile))
                        continue;
                    var size = new FileInfo(obj.IndexFile).Length;
                    var wide = obj.IndexFormat == null
                               || obj.IndexFormat.IndexOf("R32", StringComparison.OrdinalIgnoreCase) >= 0;
                    var available = (int)(size / (wide ? 4 : 2));
                    // Each draw on its own, never their sum: the branches of an if exclude one
                    // another, so adding them up always overshoots the buffer.
                    var over = obj.Draws.Count(d => d.StartIndex + d.IndexCount > available);
                    if (over > 0)
                        Warnings.Add($"{obj.Name}: {over} draw(s) reach past the end of a "
                                     + $"buffer holding {available} indices");
                }
            }
            if (Parts.Count == 0 && TextureSets.Count == 0)
                Warnings.Add("no parts found -- no *Position.buf next to the ini");
        }

        /// <summary>
        /// Files nobody mentioned. Hand-edited folders keep the buffers of parts that were
        /// dropped from a later version, and they are easy to mistake for something the mod
        /// still uses.
        ///
        /// Run once over the whole mod, not once per ini: several inis share a folder, and
        /// each on its own would report the buffers of the others as leftovers.
        /// </summary>
        private void CheckLeftovers(string folder)
        {
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(folder, "*" + PositionSuffix,
                                                    SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var stem = name.Substring(0, name.Length - PositionSuffix.Length);
                if (!Parts.Any(p => p.Name.EndsWith(stem, StringComparison.OrdinalIgnoreCase))
                    && reported.Add(stem))
                    Warnings.Add($"{stem}: buffers lie in the folder but no ini mentions "
                                 + "them -- leftovers, ignored");
            }
        }

        private static IniSection FindByFile(Dictionary<string, IniSection> resources, string file)
        {
            foreach (var resource in resources.Values)
            {
                if (string.Equals(Path.GetFileName(resource.Get("filename")), file,
                                  StringComparison.OrdinalIgnoreCase))
                    return resource;
            }
            return null;
        }

        private static int Int(string text) =>
            int.TryParse(text.Trim(), out var value) ? value : 0;
    }
}
