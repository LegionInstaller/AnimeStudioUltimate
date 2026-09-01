using System;
using System.Collections.Generic;
using System.Linq;

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
        /// <param name="include">
        /// Decides the draws guarded by an <c>if</c> in the ini. Null takes all of them.
        /// </param>
        public static ImportedMesh Build(MigotoPart part, MeshReplacementContext context,
                                         List<string> warnings, Func<string, bool> include = null)
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

            foreach (var v in vertices)
            {
                mesh.VertexList.Add(new ImportedVertex
                {
                    // X is negated here for the same reason the converter negates it on a
                    // Unity mesh: FBX is the other handedness.
                    Vertex = new Vector3(-v.PosX, v.PosY, v.PosZ),
                    Normal = new Vector3(-v.NrmX, v.NrmY, v.NrmZ),
                    Tangent = new Vector4(-v.TanX, v.TanY, v.TanZ, v.TanW),
                    Color = new Color(v.ColR / 255f, v.ColG / 255f, v.ColB / 255f, v.ColA / 255f),
                    UV = Uv(v),
                    Weights = new[] { v.W0, v.W1, v.W2, v.W3 },
                    BoneIndices = new[] { v.B0, v.B1, v.B2, v.B3 },
                });
            }

            BuildSubmeshes(part, mesh, vertices.Length, context, warnings, include);
            if (mesh.SubmeshList.Count == 0)
            {
                warnings.Add($"{part.Name}: no draw call produced any triangle");
                return null;
            }

            mesh.BoneList = BuildBones(skinned, context, warnings);
            return mesh;
        }

        private static float[][] Uv(MigotoVertex v)
        {
            var uv = new float[8][];
            uv[0] = new[] { v.U0, v.V0 };
            uv[1] = new[] { v.U1, v.V1 };
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
        /// </summary>
        private static void BuildSubmeshes(MigotoPart part, ImportedMesh mesh, int vertexCount,
                                           MeshReplacementContext context, List<string> warnings,
                                           Func<string, bool> include)
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
                    if (draw.Condition != null && include != null && !include(draw.Condition))
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

                    mesh.SubmeshList.Add(new ImportedSubmesh
                    {
                        FaceList = faces,
                        BaseVertex = 0,          // every draw indexes the one shared buffer
                        Material = context.MaterialOf?.Invoke(material),
                    });
                    material++;
                }
            }
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

        /// <summary>
        /// Reads a condition such as <c>$Garter == 1</c> against a set of switches the user
        /// turned on. Anything it cannot parse is kept, because dropping geometry on a
        /// misunderstanding is the worse mistake.
        /// </summary>
        public static Func<string, bool> Switches(IEnumerable<string> enabled)
        {
            var on = new HashSet<string>(enabled ?? Enumerable.Empty<string>(),
                                         StringComparer.OrdinalIgnoreCase);
            return condition =>
            {
                var parts = condition.Split(new[] { "==" }, StringSplitOptions.None);
                if (parts.Length != 2)
                    return true;
                var name = parts[0].Trim();
                var wanted = parts[1].Trim();
                if (!name.StartsWith("$"))
                    return true;
                var isOn = on.Contains(name);
                if (wanted == "1") return isOn;
                if (wanted == "0") return !isOn;
                return true;
            };
        }
    }
}
