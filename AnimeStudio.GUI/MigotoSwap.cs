using System;
using System.Collections.Generic;
using System.Linq;
using AnimeStudio.Migoto;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Holds the mesh replacement the user armed in <see cref="MigotoForm"/> until the next
    /// export picks it up. Nothing is armed by default, and while nothing is armed
    /// <see cref="Hook"/> is null, so the exporters behave exactly as they did before.
    ///
    /// The mapping is kept by renderer name rather than by object: the dialog runs long
    /// before the export, and a name survives a reload where a reference would not.
    /// </summary>
    public static class MigotoSwap
    {
        private static MigotoMod mod;
        private static Dictionary<string, MigotoPart> byRenderer;
        private static Func<string, bool> include;

        /// <summary>The armed mod folder, or null.</summary>
        public static string Folder => mod?.Folder;

        public static bool IsArmed => mod != null && byRenderer != null && byRenderer.Count > 0;

        /// <summary>What the last export actually swapped, for the log.</summary>
        public static List<string> LastSwapped { get; } = new List<string>();

        /// <summary>Warnings collected while reading and building, for the log.</summary>
        public static List<string> Warnings { get; } = new List<string>();

        public static void Arm(MigotoMod loaded, Dictionary<string, MigotoPart> mapping,
                               IEnumerable<string> switchesOn)
        {
            mod = loaded;
            byRenderer = mapping;
            include = MeshBuilder.Switches(switchesOn);
            LastSwapped.Clear();
            Warnings.Clear();
        }

        public static void Disarm()
        {
            mod = null;
            byRenderer = null;
            include = null;
            LastSwapped.Clear();
            Warnings.Clear();
        }

        /// <summary>
        /// The delegate handed to <c>ModelConverter.Options.replaceMesh</c>. Null while
        /// nothing is armed, which is what keeps the ordinary export untouched.
        /// </summary>
        public static Func<MeshReplacementContext, ImportedMesh> Hook =>
            IsArmed ? Replace : (Func<MeshReplacementContext, ImportedMesh>)null;

        private static ImportedMesh Replace(MeshReplacementContext context)
        {
            if (!context.Renderer.m_GameObject.TryGet(out GameObject go))
                return null;
            if (!byRenderer.TryGetValue(go.m_Name, out var part))
                return null;

            var warnings = new List<string>();
            var mesh = MeshBuilder.Build(part, context, warnings, include);
            foreach (var w in warnings)
            {
                Warnings.Add(w);
                Logger.Warning($"Mesh replacement: {w}");
            }
            if (mesh == null)
                return null;

            var line = $"{go.m_Name} <- {part.Name} ({mesh.VertexList.Count} vertices, "
                       + $"{mesh.SubmeshList.Sum(s => s.FaceList.Count)} triangles)";
            LastSwapped.Add(line);
            Logger.Info("Mesh replacement: " + line);
            return mesh;
        }

        /// <summary>
        /// Copies the mod's own textures next to the exported model. They are the point of
        /// most mods, and a mesh without them exports as a grey shape.
        /// </summary>
        public static int CopyTextures(string folder)
        {
            if (mod == null || LastSwapped.Count == 0 || string.IsNullOrEmpty(folder))
                return 0;
            var copied = 0;
            foreach (var source in mod.Textures)
            {
                try
                {
                    var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(source));
                    System.IO.File.Copy(source, target, true);
                    copied++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Mesh replacement: could not copy "
                                   + $"{System.IO.Path.GetFileName(source)}, {ex.Message}");
                }
            }
            if (copied > 0)
                Logger.Info($"Mesh replacement: copied {copied} texture(s) from the mod folder.");
            return copied;
        }

        /// <summary>
        /// Every skinned renderer in the loaded assets, by name, with its bone count -- what
        /// the dialog offers as targets and what the bone-count suggestion works on.
        /// </summary>
        public static List<(string Name, int Bones)> LoadedRenderers()
        {
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in Studio.assetsManager.assetsFileList)
            {
                foreach (var o in file.Objects)
                {
                    if (o is not SkinnedMeshRenderer smr || smr.m_Bones.Count == 0)
                        continue;
                    if (!smr.m_GameObject.TryGet(out GameObject go))
                        continue;
                    if (!found.TryGetValue(go.m_Name, out var known) || smr.m_Bones.Count > known)
                        found[go.m_Name] = smr.m_Bones.Count;
                }
            }
            return found.Select(kv => (kv.Key, kv.Value))
                        .OrderBy(x => x.Value)
                        .ThenBy(x => x.Key, StringComparer.Ordinal)
                        .ToList();
        }

        /// <summary>
        /// The renderers a part could sit on, likeliest first.
        ///
        /// The bone count only rules candidates out: a mod's BLENDINDICES address the
        /// target's <c>m_Bones</c> directly, so anything with fewer bones than the part's
        /// highest index cannot be it. Ranking by the tightest fit alone is not enough --
        /// with several characters loaded, a stranger with barely enough bones beats the
        /// right renderer with a few to spare. The name decides instead: a part called
        /// "RemielleBody" out of "Remielle.ini" shares two words with
        /// "Remielle_Origin_Body_1" and only one with "Pyrois_Body_02".
        /// </summary>
        public static List<(string Name, int Bones)> Candidates(
            MigotoPart part, string modName, List<(string Name, int Bones)> renderers,
            List<string> warnings)
        {
            var (highest, _) = part.BoneRange(warnings);
            var wanted = Tokens(part.Name);
            wanted.UnionWith(Tokens(modName));
            return renderers
                .Where(r => r.Bones > highest)
                .OrderByDescending(r => Tokens(r.Name).Count(wanted.Contains))
                .ThenBy(r => r.Bones)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The words in a name, split on underscores and on the humps of camel case, so
        /// "RemielleBody" and "Remielle_Origin_Body_1" have two in common. Pure numbers are
        /// dropped -- a shared "01" says nothing.
        /// </summary>
        private static HashSet<string> Tokens(string name)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(name))
                return set;
            var word = new System.Text.StringBuilder();
            void Flush()
            {
                if (word.Length > 1 && !word.ToString().All(char.IsDigit))
                    set.Add(word.ToString());
                word.Clear();
            }
            foreach (var c in name)
            {
                if (c == '_' || c == ' ' || c == '-' || c == '.') { Flush(); continue; }
                if (char.IsUpper(c) && word.Length > 0 && !char.IsUpper(word[word.Length - 1]))
                    Flush();
                word.Append(c);
            }
            Flush();
            return set;
        }
    }
}
