using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AnimeStudio
{
    public sealed class NapAssetBundleIndexAsset : NamedObject
    {
        public List<IndexAssetRef> m_AssetArray;
        public List<IndexBundleRef> m_BundleArray;
        public List<IndexBlockRef> m_BlockArray;
        public List<uint> m_ChildrenIndexArray;
        // Newer game versions (seen in 3.3): the direct dependencies of each bundle.
        // m_ChildrenIndexArray holds all of them, the indirect ones too.
        public List<uint> m_DirectChildrenIndexArray;

        public NapAssetBundleIndexAsset(ObjectReader reader) : base(reader)
        {
            // The layout carries no version, so take the one that uses up the object exactly.
            // A layout this reader does not know stops here instead of allocating from a wrong count.
            var direct = Fits(reader, IndexBundleRef.SizeWithDirect, 2);
            if (!direct && !Fits(reader, IndexBundleRef.Size, 1))
                throw new InvalidDataException("Unknown NapAssetBundleIndexAsset layout");

            var m_AssetArraySize = reader.ReadInt32();
            m_AssetArray = new List<IndexAssetRef>(m_AssetArraySize);
            for (int i = 0; i < m_AssetArraySize; i++)
                m_AssetArray.Add(new IndexAssetRef(reader));

            var m_BundleArraySize = reader.ReadInt32();
            m_BundleArray = new List<IndexBundleRef>(m_BundleArraySize);
            for (int i = 0; i < m_BundleArraySize; i++)
                m_BundleArray.Add(new IndexBundleRef(reader, direct));

            var m_BlockArraySize = reader.ReadInt32();
            m_BlockArray = new List<IndexBlockRef>(m_BlockArraySize);
            for (int i = 0; i < m_BlockArraySize; i++)
                m_BlockArray.Add(new IndexBlockRef(reader));

            reader.AlignStream();

            m_ChildrenIndexArray = ReadIndexArray(reader);
            if (direct)
                m_DirectChildrenIndexArray = ReadIndexArray(reader);
        }

        private static List<uint> ReadIndexArray(ObjectReader reader)
        {
            var size = reader.ReadInt32();
            var array = new List<uint>(size);
            for (int i = 0; i < size; i++)
                array.Add(reader.ReadUInt32());
            return array;
        }

        // True if the arrays, with bundle entries of this size and this many index arrays at
        // the end, fill the object to its last byte. Leaves the reader where it was.
        private static bool Fits(ObjectReader reader, int bundleSize, int indexArrays)
        {
            var start = reader.Position;
            var end = reader.byteStart + reader.byteSize;
            var fits = Skip(reader, end, IndexAssetRef.Size)
                && Skip(reader, end, bundleSize)
                && Skip(reader, end, IndexBlockRef.Size);
            if (fits)
            {
                reader.AlignStream();
                for (int i = 0; i < indexArrays && fits; i++)
                    fits = Skip(reader, end, sizeof(uint));
                fits = fits && reader.Position == end;
            }
            reader.Position = start;
            return fits;
        }

        // Steps over an array of fixed size elements. False if it does not fit into the object.
        private static bool Skip(ObjectReader reader, long end, int elementSize)
        {
            if (end - reader.Position < 4)
                return false;
            var bytes = (long)reader.ReadInt32() * elementSize;
            if (bytes < 0 || bytes > end - reader.Position)
                return false;
            reader.Position += bytes;
            return true;
        }

        public class IndexAssetRef
        {
            public const int Size = 12;

            public uint bundle;
            public long pathHash;
            public IndexAssetRef(ObjectReader reader)
            {
                bundle = reader.ReadUInt32();
                pathHash = reader.ReadInt64();
            }
        }

        public class IndexBundleRef
        {
            public const int Size = 36;
            public const int SizeWithDirect = 44;

            public uint blockIndex;
            public ulong bundleHashName;
            public ulong bundleHash;
            public uint offset;
            public uint childrenStartIndex;
            public uint childrenEndIndex;
            // Range in m_DirectChildrenIndexArray, only in the newer layout
            public uint directChildrenStartIndex;
            public uint directChildrenEndIndex;
            public uint fileSize;
            public IndexBundleRef(ObjectReader reader, bool direct)
            {
                blockIndex = reader.ReadUInt32();
                bundleHashName = reader.ReadUInt64();
                bundleHash = reader.ReadUInt64();
                offset = reader.ReadUInt32();
                childrenStartIndex = reader.ReadUInt32();
                childrenEndIndex = reader.ReadUInt32();
                if (direct)
                {
                    directChildrenStartIndex = reader.ReadUInt32();
                    directChildrenEndIndex = reader.ReadUInt32();
                }
                fileSize = reader.ReadUInt32();
            }
        }

        public class IndexBlockRef
        {
            public const int Size = 9;

            public ulong blockHashName;
            public byte location;
            public IndexBlockRef(ObjectReader reader)
            {
                blockHashName = reader.ReadUInt64();
                location = reader.ReadByte();
            }
        }
    }
}
