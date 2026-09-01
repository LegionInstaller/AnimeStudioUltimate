using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio.Migoto
{
    /// <summary>One vertex, as 3DMigoto dumped it. Still in the game's shader space.</summary>
    public struct MigotoVertex
    {
        public float PosX, PosY, PosZ;
        public float NrmX, NrmY, NrmZ;
        public float TanX, TanY, TanZ, TanW;
        public float W0, W1, W2, W3;
        public int B0, B1, B2, B3;
        public byte ColR, ColG, ColB, ColA;
        public float U0, V0;                // the real texture coordinates
        public float U1, V1;                // the second usable set, last pair in the buffer
    }

    /// <summary>
    /// Decodes the three vertex streams a ZZMI mod ships. The layouts are not written down
    /// anywhere in the mod, so they were read out of the data itself and are asserted here
    /// rather than assumed: normals and tangents come out unit length and the blend weights
    /// sum to one, which no wrong reading would produce.
    ///
    ///   Position, stride 40 : POSITION float3, NORMAL float3, TANGENT float4
    ///   Blend,    stride 32 : BLENDWEIGHTS float4, BLENDINDICES uint32 x4
    ///   Texcoord, stride 24 : COLOR 4 bytes, then five half2 -- the first is UV0, the last
    ///                         a second usable set; the ones between carry the outline data
    ///                         the toon shader needs and are not texture coordinates.
    ///
    /// Other strides are refused instead of guessed. A silently misread buffer would look
    /// like a broken mesh much later, at export time.
    /// </summary>
    public static class VertexBuffers
    {
        public const int PositionStride = 40;
        public const int BlendStride = 32;

        public static MigotoVertex[] Read(string positionFile, int positionStride,
                                          string blendFile, int blendStride,
                                          string texcoordFile, int texcoordStride,
                                          int vertexCount, List<string> warnings)
        {
            // A part whose vertex count could not be worked out arrives here as -1. Building
            // an array of that length throws an overflow rather than anything readable, so it
            // is stopped with a reason instead.
            if (vertexCount <= 0)
            {
                warnings.Add($"{System.IO.Path.GetFileName(positionFile ?? "?")}: "
                             + "the vertex count is unknown, the part cannot be read");
                return Array.Empty<MigotoVertex>();
            }

            var vertices = new MigotoVertex[vertexCount];

            ReadPositions(positionFile, positionStride, vertices, warnings);
            ReadBlend(blendFile, blendStride, vertices, warnings);
            ReadTexcoords(texcoordFile, texcoordStride, vertices, warnings);
            return vertices;
        }

        private static byte[] Open(string file, int stride, int count, string what,
                                   List<string> warnings)
        {
            if (file == null || !File.Exists(file))
            {
                warnings.Add($"{what}: file missing");
                return null;
            }
            var data = File.ReadAllBytes(file);
            if (stride <= 0)
            {
                warnings.Add($"{what}: no stride given");
                return null;
            }
            if (data.Length % stride != 0)
            {
                warnings.Add($"{what}: {data.Length} bytes is not a multiple of stride {stride}");
                return null;
            }
            var have = data.Length / stride;
            if (have < count)
            {
                warnings.Add($"{what}: holds {have} vertices but {count} were declared");
                return null;
            }
            if (have > count)
                warnings.Add($"{what}: holds {have} vertices, only the first {count} are used");
            return data;
        }

        private static void ReadPositions(string file, int stride, MigotoVertex[] vertices,
                                          List<string> warnings)
        {
            var data = Open(file, stride, vertices.Length, "position buffer", warnings);
            if (data == null)
                return;
            if (stride != PositionStride)
            {
                warnings.Add($"position buffer: stride {stride} is not the known layout "
                             + $"({PositionStride}: position, normal, tangent)");
                return;
            }
            for (int i = 0; i < vertices.Length; i++)
            {
                var at = i * stride;
                vertices[i].PosX = BitConverter.ToSingle(data, at);
                vertices[i].PosY = BitConverter.ToSingle(data, at + 4);
                vertices[i].PosZ = BitConverter.ToSingle(data, at + 8);
                vertices[i].NrmX = BitConverter.ToSingle(data, at + 12);
                vertices[i].NrmY = BitConverter.ToSingle(data, at + 16);
                vertices[i].NrmZ = BitConverter.ToSingle(data, at + 20);
                vertices[i].TanX = BitConverter.ToSingle(data, at + 24);
                vertices[i].TanY = BitConverter.ToSingle(data, at + 28);
                vertices[i].TanZ = BitConverter.ToSingle(data, at + 32);
                vertices[i].TanW = BitConverter.ToSingle(data, at + 36);
            }
            CheckUnitLength(vertices, warnings);
        }

        /// <summary>
        /// Confirms the reading rather than trusting it: if the six floats behind the
        /// position were not a normal and a tangent, their lengths would not be one.
        /// </summary>
        private static void CheckUnitLength(MigotoVertex[] vertices, List<string> warnings)
        {
            var step = Math.Max(1, vertices.Length / 512);
            int checkedCount = 0, offNormal = 0;
            for (int i = 0; i < vertices.Length; i += step)
            {
                var v = vertices[i];
                var length = Math.Sqrt(v.NrmX * v.NrmX + v.NrmY * v.NrmY + v.NrmZ * v.NrmZ);
                checkedCount++;
                if (Math.Abs(length - 1.0) > 0.05)
                    offNormal++;
            }
            if (offNormal > checkedCount / 20)
                warnings.Add($"position buffer: {offNormal} of {checkedCount} sampled normals "
                             + "are not unit length -- the layout may not be what is assumed");
        }

        private static void ReadBlend(string file, int stride, MigotoVertex[] vertices,
                                      List<string> warnings)
        {
            var data = Open(file, stride, vertices.Length, "blend buffer", warnings);
            if (data == null)
                return;
            if (stride != BlendStride)
            {
                warnings.Add($"blend buffer: stride {stride} is not the known layout "
                             + $"({BlendStride}: four weights, four 32-bit indices)");
                return;
            }
            var offWeight = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                var at = i * stride;
                vertices[i].W0 = BitConverter.ToSingle(data, at);
                vertices[i].W1 = BitConverter.ToSingle(data, at + 4);
                vertices[i].W2 = BitConverter.ToSingle(data, at + 8);
                vertices[i].W3 = BitConverter.ToSingle(data, at + 12);
                vertices[i].B0 = (int)BitConverter.ToUInt32(data, at + 16);
                vertices[i].B1 = (int)BitConverter.ToUInt32(data, at + 20);
                vertices[i].B2 = (int)BitConverter.ToUInt32(data, at + 24);
                vertices[i].B3 = (int)BitConverter.ToUInt32(data, at + 28);

                var sum = vertices[i].W0 + vertices[i].W1 + vertices[i].W2 + vertices[i].W3;
                if (Math.Abs(sum - 1f) > 0.01f)
                    offWeight++;
            }
            if (offWeight > vertices.Length / 20)
                warnings.Add($"blend buffer: {offWeight} of {vertices.Length} vertices have "
                             + "weights that do not sum to one -- the layout may be wrong");
        }

        private static void ReadTexcoords(string file, int stride, MigotoVertex[] vertices,
                                          List<string> warnings)
        {
            var data = Open(file, stride, vertices.Length, "texcoord buffer", warnings);
            if (data == null)
                return;
            // Four bytes of colour, then as many half2 as fit. Anything else is unknown.
            if (stride < 8 || (stride - 4) % 4 != 0)
            {
                warnings.Add($"texcoord buffer: stride {stride} is not colour plus half2 pairs");
                return;
            }
            var pairs = (stride - 4) / 4;
            var lastPair = 4 + (pairs - 1) * 4;
            for (int i = 0; i < vertices.Length; i++)
            {
                var at = i * stride;
                vertices[i].ColR = data[at];
                vertices[i].ColG = data[at + 1];
                vertices[i].ColB = data[at + 2];
                vertices[i].ColA = data[at + 3];
                vertices[i].U0 = Half(data, at + 4);
                vertices[i].V0 = Half(data, at + 6);
                vertices[i].U1 = Half(data, at + lastPair);
                vertices[i].V1 = Half(data, at + lastPair + 2);
            }
        }

        private static float Half(byte[] data, int at) =>
            (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, at));

        /// <summary>Reads an index buffer. ZZMI writes 32-bit indices; 16-bit is accepted too.</summary>
        public static int[] ReadIndices(string file, string format, List<string> warnings)
        {
            if (file == null || !File.Exists(file))
            {
                warnings.Add($"index buffer {System.IO.Path.GetFileName(file ?? "?")}: file missing");
                return Array.Empty<int>();
            }
            var data = File.ReadAllBytes(file);
            var wide = format == null || format.IndexOf("R32", StringComparison.OrdinalIgnoreCase) >= 0;
            var size = wide ? 4 : 2;
            if (data.Length % size != 0)
            {
                warnings.Add($"index buffer {System.IO.Path.GetFileName(file)}: "
                             + $"{data.Length} bytes is not a multiple of {size}");
                return Array.Empty<int>();
            }
            var indices = new int[data.Length / size];
            for (int i = 0; i < indices.Length; i++)
                indices[i] = wide ? (int)BitConverter.ToUInt32(data, i * 4)
                                  : BitConverter.ToUInt16(data, i * 2);
            return indices;
        }
    }
}
