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

        /// <summary>The <c>if</c> this draw sat inside, or null when it always runs.</summary>
        public string Condition { get; set; }

        public bool IsOptional => Condition != null;

        public override string ToString() =>
            $"{IndexCount} indices from {StartIndex}"
            + (Condition == null ? "" : $"  (only when {Condition})");
    }

    /// <summary>An index buffer of one part, with the draws that use it.</summary>
    public sealed class MigotoObject
    {
        public string Name { get; set; }
        public string IndexFile { get; set; }
        public string IndexFormat { get; set; }
        public int MatchFirstIndex { get; set; }
        public List<MigotoDraw> Draws { get; } = new List<MigotoDraw>();

        public override string ToString() => $"{Name}: {Draws.Count} draw(s)";
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
            var used = new HashSet<int>();
            var highest = -1;
            foreach (var v in Vertices(warnings))
            {
                if (v.W0 > 0) { used.Add(v.B0); highest = Math.Max(highest, v.B0); }
                if (v.W1 > 0) { used.Add(v.B1); highest = Math.Max(highest, v.B1); }
                if (v.W2 > 0) { used.Add(v.B2); highest = Math.Max(highest, v.B2); }
                if (v.W3 > 0) { used.Add(v.B3); highest = Math.Max(highest, v.B3); }
            }
            return (highest, used.Count);
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
        public List<MigotoPart> Parts { get; } = new List<MigotoPart>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>The <c>$Name</c> variables the ini toggles draws with.</summary>
        public List<string> Switches { get; } = new List<string>();

        /// <summary>The image files the ini binds -- everything that is not a buffer.</summary>
        public List<string> Textures { get; } = new List<string>();

        private const string PositionSuffix = "Position.buf";

        public static MigotoMod Load(string iniPath)
        {
            var mod = new MigotoMod
            {
                IniPath = iniPath,
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

            foreach (var section in ini.Sections)
            {
                foreach (var entry in section.Entries)
                {
                    if (entry.Key.StartsWith("$") && !mod.Switches.Contains(entry.Key))
                        mod.Switches.Add(entry.Key);
                }
            }

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
            mod.Check();
            return mod;
        }

        private void BuildParts(ModIni ini, Dictionary<string, IniSection> resources)
        {
            foreach (var resource in resources.Values)
            {
                var file = resource.Get("filename");
                if (!file.EndsWith(PositionSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var stem = file.Substring(0, file.Length - PositionSuffix.Length);
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

        private void AttachObjects(ModIni ini, Dictionary<string, IniSection> resources)
        {
            foreach (var resource in resources)
            {
                var file = resource.Value.Get("filename");
                if (!file.EndsWith(".ib", StringComparison.OrdinalIgnoreCase))
                    continue;

                var part = LongestMatch(file);
                if (part == null)
                {
                    Warnings.Add($"index buffer {file} belongs to no known part");
                    continue;
                }

                var obj = new MigotoObject
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    IndexFile = Path.Combine(Folder, file),
                    IndexFormat = resource.Value.Get("format"),
                };

                // Every override that binds this resource as its index buffer contributes
                // its draw calls -- that is how one buffer ends up split across materials.
                foreach (var section in ini.Sections)
                {
                    var bound = section.Get("ib");
                    if (bound == null
                        || !bound.Equals(resource.Key, StringComparison.OrdinalIgnoreCase))
                        continue;

                    obj.MatchFirstIndex = section.GetInt("match_first_index", 0);
                    foreach (var entry in section.All("drawindexed"))
                    {
                        var parts = entry.Value.Split(',');
                        if (parts.Length < 2)
                            continue;
                        obj.Draws.Add(new MigotoDraw
                        {
                            IndexCount = Int(parts[0]),
                            StartIndex = Int(parts[1]),
                            BaseVertex = parts.Length > 2 ? Int(parts[2]) : 0,
                            Condition = entry.Condition,
                        });
                    }
                }
                part.Objects.Add(obj);
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
                    var covered = obj.Draws.Sum(d => d.IndexCount);
                    if (obj.Draws.Count == 0 || !File.Exists(obj.IndexFile))
                        continue;
                    var size = new FileInfo(obj.IndexFile).Length;
                    var wide = obj.IndexFormat == null
                               || obj.IndexFormat.IndexOf("R32", StringComparison.OrdinalIgnoreCase) >= 0;
                    var available = (int)(size / (wide ? 4 : 2));
                    if (covered > available)
                        Warnings.Add($"{obj.Name}: draws {covered} indices but the buffer "
                                     + $"holds {available}");
                }
            }
            if (Parts.Count == 0)
                Warnings.Add("no parts found -- no *Position.buf next to the ini");

            // Files the ini does not mention at all. Hand-edited folders keep the buffers of
            // parts that were dropped from a later version, and they are easy to mistake for
            // something the mod still uses.
            foreach (var file in Directory.GetFiles(Folder, "*" + PositionSuffix))
            {
                var name = Path.GetFileName(file);
                var stem = name.Substring(0, name.Length - PositionSuffix.Length);
                if (!Parts.Any(p => string.Equals(p.Name, stem, StringComparison.OrdinalIgnoreCase)))
                    Warnings.Add($"{stem}: buffers lie in the folder but the ini never "
                                 + "mentions them -- leftovers, ignored");
            }
        }

        private static IniSection FindByFile(Dictionary<string, IniSection> resources, string file)
        {
            foreach (var resource in resources.Values)
            {
                if (string.Equals(resource.Get("filename"), file, StringComparison.OrdinalIgnoreCase))
                    return resource;
            }
            return null;
        }

        private static int Int(string text) =>
            int.TryParse(text.Trim(), out var value) ? value : 0;
    }
}
