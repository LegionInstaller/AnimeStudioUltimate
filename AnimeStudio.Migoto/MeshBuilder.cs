using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimeStudio.Migoto
{
    /// <summary>
    /// Turns a decoded mod part into the <see cref="ImportedMesh"/> the FBX writer expects.
    ///
    /// Two things make this simpler than it looks. The vertex positions need no conversion --
    /// a Migoto buffer sits in the same space as Unity's mesh data, measured against the
    /// shipped mesh to within eight millimetres. And BLENDINDICES address the target
    /// renderer's <c>m_Bones</c> directly, so the skinning carries over by index.
    ///
    /// What does have to be reproduced is the converter's own handedness flip: it negates X
    /// on positions, normals and tangents, reverses the winding to match, and mirrors the
    /// bind poses. A replacement that skipped that would come out inside out.
    /// </summary>
    public static class MeshBuilder
    {
        /// <summary>
        /// Builds the replacement. Returns null with a reason in <paramref name="warnings"/>
        /// when the part cannot stand in for this renderer -- never a half-built mesh.
        /// </summary>
        /// <param name="chosen">
        /// The value picked for each of the mod's variables. Null takes every draw, which is
        /// only right for a mod that has no variants.
        /// </param>
        /// <param name="split">
        /// Takes every draw whatever the variables say, and keeps the variants apart: what is
        /// always drawn stays on the mesh returned, and each variant goes to
        /// <see cref="MeshReplacementContext.AddChild"/> as a mesh of its own. Without that
        /// callback there is nowhere to put them and the switch does nothing.
        /// </param>
        /// <param name="sheet">
        /// Takes down what each variant's mesh and each material depends on, for the
        /// switches that go with a split export.
        /// </param>
        public static ImportedMesh Build(MigotoPart part, MeshReplacementContext context,
                                         List<string> warnings,
                                         IReadOnlyDictionary<string, string> chosen = null,
                                         bool split = false, VariantSheet sheet = null)
        {
            if (context.Renderer is not SkinnedMeshRenderer skinned)
            {
                warnings.Add($"{part.Name}: the target is not a skinned renderer");
                return null;
            }

            var bones = skinned.m_Bones;
            var (highest, _) = part.BoneRange(warnings);
            if (highest >= bones.Count)
            {
                warnings.Add($"{part.Name}: weights bone {highest}, but the renderer has only "
                             + $"{bones.Count} -- wrong target");
                return null;
            }

            var vertices = part.Vertices(warnings);
            if (vertices.Length == 0)
            {
                warnings.Add($"{part.Name}: no vertices could be read");
                return null;
            }

            var mesh = new ImportedMesh
            {
                Path = PathOfRenderer(skinned, context),
                hasNormal = true,
                hasTangent = true,
                hasColor = true,
                hasUV = new bool[8],
                uvType = new int[8],
                VertexList = new List<ImportedVertex>(vertices.Length),
                SubmeshList = new List<ImportedSubmesh>(),
            };
            // uvType is the FBX texture channel a set is attached to, not its index: 0 is
            // the diffuse channel, which is the one an importer treats as the main UV map.
            mesh.hasUV[0] = true;
            mesh.hasUV[1] = true;
            mesh.uvType[0] = 0;
            mesh.uvType[1] = 1;

            var dropped = 0;
            foreach (var v in vertices)
            {
                // An influence past the end of the target's bone list. BoneRange already
                // established that nothing needs it -- otherwise this part would have been
                // refused above -- so it is left out instead of writing an index the FBX
                // cannot resolve.
                var weights = new[] { v.W0, v.W1, v.W2, v.W3 };
                var indices = new[] { v.B0, v.B1, v.B2, v.B3 };
                var lost = 0f;
                for (int k = 0; k < 4; k++)
                {
                    if (indices[k] < bones.Count && indices[k] >= 0)
                        continue;
                    if (weights[k] > 0)
                        dropped++;
                    lost += Math.Max(weights[k], 0);
                    weights[k] = 0;
                    indices[k] = 0;
                }
                // What is left of a vertex that only had a few percent out there no longer
                // comes to one, so it is brought back up.
                var kept = weights[0] + weights[1] + weights[2] + weights[3];
                if (lost > 0 && kept > 0 && kept < 0.99f)
                    for (int k = 0; k < 4; k++)
                        weights[k] /= kept;
                mesh.VertexList.Add(new ImportedVertex
                {
                    // X is negated here for the same reason the converter negates it on a
                    // Unity mesh: FBX is the other handedness.
                    Vertex = new Vector3(-v.PosX, v.PosY, v.PosZ),
                    Normal = new Vector3(-v.NrmX, v.NrmY, v.NrmZ),
                    Tangent = new Vector4(-v.TanX, v.TanY, v.TanZ, v.TanW),
                    Color = new Color(v.ColR / 255f, v.ColG / 255f, v.ColB / 255f, v.ColA / 255f),
                    UV = Uv(v),
                    Weights = weights,
                    BoneIndices = indices,
                });
            }
            if (dropped > 0)
                warnings.Add($"{part.Name}: {dropped} weight(s) point past the "
                             + $"{bones.Count} bones of the target and were left out -- the "
                             + "vertices hold nearly all their weight without them");

            var variants = split && context.AddChild != null
                ? new Dictionary<string, Variant>(StringComparer.OrdinalIgnoreCase)
                : null;
            BuildSubmeshes(part, mesh, vertices.Length, context, warnings, chosen, variants, sheet);
            if (mesh.SubmeshList.Count == 0 && (variants == null || variants.Count == 0))
            {
                warnings.Add($"{part.Name}: no draw call produced any triangle");
                return null;
            }

            mesh.BoneList = BuildBones(skinned, context, warnings);
            if (variants != null)
                HandOver(part, mesh, variants, skinned, context, warnings, sheet);
            return mesh;
        }

        /// <summary>
        /// Gives each variant to the converter as a child of the renderer, and cuts every
        /// mesh down to the vertices it uses. They all start out on the part's whole buffer,
        /// and a mod with thirty variants would otherwise write that buffer thirty times.
        /// </summary>
        private static void HandOver(MigotoPart part, ImportedMesh mesh,
                                     Dictionary<string, Variant> variants,
                                     SkinnedMeshRenderer skinned, MeshReplacementContext context,
                                     List<string> warnings, VariantSheet sheet)
        {
            var renderer = skinned.m_GameObject.TryGet(out var go) ? go.m_Name : part.Name;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Variants made of the very same draws are one mesh. A mod that repeats a draw
            // for every level of a shader effect has one piece of cloth there, not seven,
            // and it is named by what the conditions of all of them share. Sharing nothing,
            // they stay apart.
            foreach (var same in variants.Values.GroupBy(v => v.Draws))
            {
                var shared = same.First().Pieces
                    .Where(p => same.All(v => v.Pieces.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    .ToArray();
                foreach (var variant in shared.Length > 0 ? same.Take(1) : same)
                {
                    // 63 is as long as a name gets in Blender, which would cut it there
                    // anyway -- and then two variants could end up with the same one.
                    var label = string.Join(".", shared.Length > 0 ? shared : variant.Pieces);
                    var name = renderer + "." + label;
                    if (name.Length > 63)
                        name = name.Substring(0, 63);
                    for (var n = 2; !taken.Add(name); n++)
                    {
                        var suffix = "~" + n;
                        name = name.Substring(0, Math.Min(name.Length, 63 - suffix.Length)) + suffix;
                    }
                    if (!name.EndsWith(label, StringComparison.Ordinal))
                        warnings.Add($"{part.Name}: the variant {label} is called {name}, "
                                     + "its full name is too long");

                    Compact(variant.Mesh);
                    variant.Mesh.BoneList = mesh.BoneList;
                    context.AddChild(name, variant.Mesh);
                    // One mesh for several variants shows in the case of any of them.
                    sheet?.AddMesh(variant.Mesh.Path,
                                   (shared.Length > 0 ? same.AsEnumerable() : new[] { variant })
                                       .Select(v => v.Conditions));
                }
            }
            Compact(mesh);
        }

        /// <summary>The draws of one variant, on their way to becoming a mesh.</summary>
        private sealed class Variant
        {
            public string[] Pieces;         // what its name is made of, one per condition
            public ImportedMesh Mesh;
            public string Draws = "";       // which draws it holds, and in what material
            public List<IniCondition> Conditions;   // what its draws sit behind
        }

        /// <summary>A mesh for a variant: the same buffer and layout, no triangles yet.</summary>
        private static ImportedMesh EmptyLike(ImportedMesh mesh) => new ImportedMesh
        {
            hasNormal = mesh.hasNormal,
            hasTangent = mesh.hasTangent,
            hasColor = mesh.hasColor,
            hasUV = mesh.hasUV,
            uvType = mesh.uvType,
            VertexList = mesh.VertexList,
            SubmeshList = new List<ImportedSubmesh>(),
        };

        /// <summary>Drops the vertices no triangle of the mesh uses. Their order stays.</summary>
        private static void Compact(ImportedMesh mesh)
        {
            var place = new int[mesh.VertexList.Count];
            foreach (var submesh in mesh.SubmeshList)
                foreach (var face in submesh.FaceList)
                    foreach (var index in face.VertexIndices)
                        place[index] = 1;

            var kept = new List<ImportedVertex>();
            for (int i = 0; i < place.Length; i++)
            {
                if (place[i] == 0)
                    continue;
                place[i] = kept.Count;
                kept.Add(mesh.VertexList[i]);
            }
            foreach (var submesh in mesh.SubmeshList)
                foreach (var face in submesh.FaceList)
                    for (int k = 0; k < face.VertexIndices.Length; k++)
                        face.VertexIndices[k] = place[face.VertexIndices[k]];
            mesh.VertexList = kept;
        }

        /// <summary>
        /// The variant a draw belongs to, by what its conditions ask: "Garter=1",
        /// "Hair!=0", one piece for each. None for a draw that is always made, null for
        /// one whose conditions rule each other out.
        ///
        /// A condition the reader could not make sense of names its variant by its own text.
        /// It cannot be judged, but it can be kept apart, and that is all that is needed here.
        /// The mod's on-switch is no variant: it is judged as it would be otherwise.
        /// </summary>
        private static string[] VariantOf(MigotoDraw draw, IReadOnlyDictionary<string, string> chosen)
        {
            // The unread ones go last. They are the long ones, and a name that has to be cut
            // short should lose their end rather than a value that tells two variants apart.
            var pieces = new List<string>();
            var unread = new List<string>();
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in draw.Conditions)
            {
                if (c.Opaque || c.Variable == null)
                {
                    if (named.Add(c.Text))
                        unread.Add("(" + Tidy(c.Text) + ")");
                    continue;
                }
                if (IsOnSwitch(c.Variable))
                {
                    if (chosen != null && !c.Holds(chosen))
                        return null;
                    continue;
                }
                if (!named.Add(c.Variable))
                    continue;           // named when the variable first came up

                var same = draw.Conditions
                    .Where(o => !o.Opaque && string.Equals(o.Variable, c.Variable,
                                                           StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var equal = same.Where(o => !o.Negate).Select(o => o.Value)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var unequal = same.Where(o => o.Negate).Select(o => o.Value)
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (equal.Count > 1
                    || (equal.Count == 1 && unequal.Contains(equal[0], StringComparer.OrdinalIgnoreCase)))
                    return null;
                pieces.Add(Tidy(c.Variable)
                           + (equal.Count == 1 ? "=" + equal[0] : "!=" + string.Join(",", unequal)));
            }
            return pieces.Concat(unread).ToArray();
        }

        private static bool IsOnSwitch(string variable) =>
            string.Equals(variable, "$active", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A condition as a name: variables without their dollar and namespace, no blanks,
        /// the operators single. A slash would split the node's path, so none is left.
        /// </summary>
        private static string Tidy(string text)
        {
            var name = Regex.Replace(text, @"\$([^\s\\]*\\)*", "");
            name = Regex.Replace(name, @"\s+", "");
            return name.Replace("==", "=").Replace("&&", "&").Replace("||", "|")
                       .Replace('/', '_').Replace('\\', '_');
        }

        /// <summary>
        /// The chosen values, moved to where a draw's own conditions hold. The images a
        /// variant wears are the ones the mod binds in its case, not in the chosen one.
        /// </summary>
        private static Dictionary<string, string> ValuesFor(MigotoDraw draw,
                                                            IReadOnlyList<MigotoTexture> textures,
                                                            IReadOnlyDictionary<string, string> chosen)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in chosen)
                values[pair.Key] = pair.Value;

            var asked = draw.Conditions.Where(c => !c.Opaque && c.Variable != null
                                                   && !IsOnSwitch(c.Variable)).ToList();
            foreach (var c in asked.Where(a => !a.Negate))
                values[c.Variable] = c.Value;
            foreach (var c in asked.Where(a => a.Negate && !a.Holds(values)))
            {
                // "Anything but": the first value the textures ask about that the draw allows.
                // If they ask about none, one that matches nothing does as well as any.
                var barred = asked.Where(o => o.Negate && string.Equals(o.Variable, c.Variable,
                                                 StringComparison.OrdinalIgnoreCase))
                                  .Select(o => o.Value).ToList();
                values[c.Variable] = textures
                    .SelectMany(t => t.Conditions)
                    .Where(o => !o.Opaque && string.Equals(o.Variable, c.Variable,
                                                           StringComparison.OrdinalIgnoreCase))
                    .Select(o => o.Value)
                    .FirstOrDefault(v => !barred.Contains(v, StringComparer.OrdinalIgnoreCase)) ?? "";
            }

            // An "or" the values do not meet yet is met by the first of its sides that
            // leaves the plain conditions standing.
            foreach (var c in draw.Conditions.Where(o => o.AnyOf != null && !o.Negate))
            {
                if (c.Holds(values))
                    continue;
                foreach (var side in c.AnyOf)
                {
                    var tried = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                    foreach (var term in side.Where(t => !t.Negate))
                        tried[term.Variable] = term.Value;
                    if (!c.Holds(tried) || !asked.All(a => a.Holds(tried)))
                        continue;
                    values = tried;
                    break;
                }
            }
            return values;
        }

        /// <summary>
        /// The texture coordinates, with v turned around.
        ///
        /// A Migoto buffer holds them the way Direct3D wants them: v = 0 is the top row of
        /// the image. FBX, and every importer reading it, puts v = 0 at the bottom. Without
        /// the flip the texture lands mirrored across the middle of the page -- which on a
        /// mostly black texture still looks almost right, and on a busy one is unmistakable.
        /// Measured on both test mods by rendering the mod's own diffuse: only the flipped
        /// set puts skin on the arms and the corset where the corset belongs.
        /// </summary>
        private static float[][] Uv(MigotoVertex v)
        {
            var uv = new float[8][];
            uv[0] = new[] { v.U0, 1f - v.V0 };
            uv[1] = new[] { v.U1, 1f - v.V1 };
            return uv;
        }

        private static string PathOfRenderer(SkinnedMeshRenderer skinned, MeshReplacementContext c)
        {
            if (c.PathOf != null && skinned.m_GameObject.TryGet(out var go) && go.m_Transform != null)
                return c.PathOf(go.m_Transform);
            return null;
        }

        /// <summary>
        /// One submesh per draw call. A draw is a slice of the index buffer, which is exactly
        /// how the game splits a part across materials, so the mapping is one to one and the
        /// optional draws behind an <c>if</c> stay separable.
        ///
        /// With <paramref name="variants"/> given, nothing is left out for the values chosen:
        /// a draw behind an <c>if</c> goes to the mesh of its variant instead, made here the
        /// first time one is needed.
        /// </summary>
        private static void BuildSubmeshes(MigotoPart part, ImportedMesh mesh, int vertexCount,
                                           MeshReplacementContext context, List<string> warnings,
                                           IReadOnlyDictionary<string, string> chosen,
                                           Dictionary<string, Variant> variants, VariantSheet sheet)
        {
            var material = 0;
            foreach (var obj in part.Objects)
            {
                if (obj.Draws.Count == 0)
                    continue;
                var indices = VertexBuffers.ReadIndices(obj.IndexFile, obj.IndexFormat, warnings);
                if (indices.Length == 0)
                    continue;

                foreach (var draw in obj.Draws)
                {
                    string[] pieces = null;
                    if (variants != null)
                    {
                        pieces = VariantOf(draw, chosen);
                        if (pieces == null)
                            continue;
                    }
                    else if (chosen != null && !draw.Applies(chosen))
                        continue;
                    if (draw.IndexCount % 3 != 0)
                        warnings.Add($"{obj.Name}: a draw of {draw.IndexCount} indices is not "
                                     + "a whole number of triangles, the remainder is dropped");

                    var faces = new List<ImportedFace>(draw.IndexCount / 3);
                    var dropped = 0;
                    for (int i = 0; i + 2 < draw.IndexCount; i += 3)
                    {
                        var at = draw.StartIndex + i;
                        if (at + 2 >= indices.Length) { dropped++; continue; }
                        var a = indices[at] + draw.BaseVertex;
                        var b = indices[at + 1] + draw.BaseVertex;
                        var c = indices[at + 2] + draw.BaseVertex;
                        if (a >= vertexCount || b >= vertexCount || c >= vertexCount
                            || a < 0 || b < 0 || c < 0)
                        {
                            dropped++;
                            continue;
                        }
                        // Reversed, to match the negated X above.
                        faces.Add(new ImportedFace { VertexIndices = new[] { c, b, a } });
                    }
                    if (dropped > 0)
                        warnings.Add($"{obj.Name}: {dropped} triangle(s) point outside the "
                                     + "vertex buffer and were dropped");
                    if (faces.Count == 0)
                        continue;

                    Variant own = null;
                    var label = pieces == null ? "" : string.Join(".", pieces);
                    if (label.Length > 0 && !variants.TryGetValue(label, out own))
                        variants[label] = own = new Variant
                        { Pieces = pieces, Mesh = EmptyLike(mesh), Conditions = draw.Conditions };

                    var submesh = new ImportedSubmesh
                    {
                        FaceList = faces,
                        BaseVertex = 0,          // every draw indexes the one shared buffer
                        Material = (own == null
                                       ? ModMaterial(obj, context, warnings, chosen, sheet)
                                       : VariantMaterial(obj, draw, label, context, warnings, chosen, sheet))
                                   ?? context.MaterialOf?.Invoke(material),
                    };
                    (own?.Mesh ?? mesh).SubmeshList.Add(submesh);
                    // A variant's draw comes on top of what the original has, it does not
                    // take the place of one of its submeshes.
                    if (own == null)
                        material++;
                    else
                        own.Draws += $"{obj.Name}:{draw.StartIndex}:{draw.IndexCount}:"
                                     + $"{draw.BaseVertex}:{submesh.Material};";
                }
            }
        }

        /// <summary>
        /// The material of a variant's draw. Most variants only add geometry and wear what
        /// the rest of the object wears. One whose own values make the mod bind other images
        /// gets a material of its own, named after it.
        /// </summary>
        private static string VariantMaterial(MigotoObject obj, MigotoDraw draw, string variant,
                                              MeshReplacementContext context, List<string> warnings,
                                              IReadOnlyDictionary<string, string> chosen,
                                              VariantSheet sheet)
        {
            if (chosen == null)
                return ModMaterial(obj, context, warnings, null, sheet);

            var values = ValuesFor(draw, obj.Textures, chosen);
            var same = Slots.All(slot => MigotoTexture.Pick(obj.Textures, slot.Name, values)
                                         == MigotoTexture.Pick(obj.Textures, slot.Name, chosen));
            return same
                ? ModMaterial(obj, context, warnings, chosen, sheet)
                : Material(obj.Name + "." + variant, obj.Textures, context, warnings, values, sheet);
        }

        /// <summary>The two texture slots an FBX material has a place for.</summary>
        private static readonly (string Name, int Dest)[] Slots = { ("Diffuse", 0), ("NormalMap", 1) };

        /// <summary>
        /// A material carrying the mod's own images, or null when the mod binds none for this
        /// object -- then the original's material stands, as it did before.
        ///
        /// It has to be the mod's textures: a mod re-maps its geometry for the images it
        /// ships, so the game's texture on the mod's UVs is not an approximation but a
        /// different layout altogether. Measured on the Remielle mod against the shipped
        /// mesh, the same point on the body sits 0.58 apart in u.
        ///
        /// Only the two slots an FBX has a place for are linked. The light and material maps
        /// belong to the game's toon shader, which no importer reconstructs; they are copied
        /// next to the model and left for whoever wants them.
        /// </summary>
        private static string ModMaterial(MigotoObject obj, MeshReplacementContext context,
                                          List<string> warnings,
                                          IReadOnlyDictionary<string, string> chosen,
                                          VariantSheet sheet) =>
            Material(obj.Name, obj.Textures, context, warnings, chosen, sheet);

        /// <summary>
        /// The same material, built from a bare list of bindings. A mod that only repaints a
        /// piece -- a face, say -- has no geometry to hang them off, so the images arrive on
        /// their own.
        /// </summary>
        public static string Material(string forName, IReadOnlyList<MigotoTexture> bindings,
                                      MeshReplacementContext context, List<string> warnings,
                                      IReadOnlyDictionary<string, string> chosen,
                                      VariantSheet sheet = null)
        {
            if (context.Materials == null || context.Textures == null || bindings.Count == 0)
                return null;

            var name = "MOD_" + forName;
            sheet?.AddMaterial(name, bindings);
            if (context.Materials.Any(m => m.Name == name))
                return name;

            var textures = new List<ImportedMaterialTexture>();
            foreach (var slot in Slots)
            {
                var path = MigotoTexture.Pick(bindings, slot.Name, chosen);
                if (path == null)
                    continue;
                var existing = context.Textures.FirstOrDefault(
                    t => t.Name == System.IO.Path.GetFileNameWithoutExtension(path) + ".png");
                var texture = existing ?? ModTextures.Load(path, warnings);
                if (texture == null)
                    continue;
                if (existing == null)
                    context.Textures.Add(texture);
                textures.Add(new ImportedMaterialTexture
                {
                    Name = texture.Name,
                    Dest = slot.Dest,
                    Offset = new Vector2(0, 0),
                    Scale = new Vector2(1, 1),
                });
            }
            if (textures.Count == 0)
                return null;

            context.Materials.Add(new ImportedMaterial
            {
                Name = name,
                Diffuse = new Color(1, 1, 1, 1),
                Ambient = new Color(0, 0, 0, 1),
                Specular = new Color(0, 0, 0, 1),
                Emissive = new Color(0, 0, 0, 1),
                Reflection = new Color(0, 0, 0, 1),
                Shininess = 0,
                Transparency = 0,
                Textures = textures,
            });
            return name;
        }

        /// <summary>
        /// The bone list is taken from the target, not from the mod: the mod has no skeleton
        /// of its own, only indices into this one. The bind poses come from the mesh that is
        /// being replaced, which is what makes the swap possible at all.
        /// </summary>
        private static List<ImportedBone> BuildBones(SkinnedMeshRenderer skinned,
                                                     MeshReplacementContext context,
                                                     List<string> warnings)
        {
            var bindPose = context.Original?.m_BindPose;
            if (bindPose == null || bindPose.Length != skinned.m_Bones.Count)
            {
                warnings.Add("the original mesh has no bind poses matching the bone list -- "
                             + "the replacement will not be skinned");
                return null;
            }

            var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
            var list = new List<ImportedBone>(skinned.m_Bones.Count);
            for (int i = 0; i < skinned.m_Bones.Count; i++)
            {
                var bone = new ImportedBone();
                if (skinned.m_Bones[i].TryGet(out var transform) && context.PathOf != null)
                    bone.Path = context.PathOf(transform);
                bone.Matrix = convert * bindPose[i] * convert;
                list.Add(bone);
            }
            return list;
        }

    }
}
