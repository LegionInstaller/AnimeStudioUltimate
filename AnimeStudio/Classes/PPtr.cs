using System;
using System.IO;
using System.Collections.Generic;

namespace AnimeStudio
{
    public sealed class PPtr<T> : IYAMLExportable where T : Object
    {
        public int m_FileID;
        public long m_PathID;

        private SerializedFile assetsFile;
        private int index = -2; //-2 - Prepare, -1 - Missing
        
        public string Name => TryGet(out var obj) ? obj.Name : string.Empty;

        public PPtr(int m_FileID,  long m_PathID, SerializedFile assetsFile)
        {
            this.m_FileID = m_FileID;
            this.m_PathID = m_PathID;
            this.assetsFile = assetsFile;
        }

        public PPtr(ObjectReader reader)
        {
            m_FileID = reader.ReadInt32();
            m_PathID = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? reader.ReadInt32() : reader.ReadInt64();
            assetsFile = reader.assetsFile;
        }

        public YAMLNode ExportYAML(int[] version)
        {
            var node = new YAMLMappingNode();
            node.Style = MappingStyle.Flow;
            node.Add("fileID", m_FileID);
            return node;
        }

        /// <summary>
        /// Chooses which loaded file a CAB name means. Several can carry the same name, so
        /// the copy from the same container as the file holding the reference wins; with a
        /// single candidate -- the overwhelmingly common case -- this is the old behaviour.
        /// The candidate list is cached, never the choice: caching the choice let the first
        /// PPtr to ask fix the answer for every other file.
        /// </summary>
        private static int Pick(int[] candidates, List<SerializedFile> files, SerializedFile from)
        {
            if (candidates.Length == 0)
            {
                return -1;
            }
            if (candidates.Length == 1)
            {
                return candidates[0];
            }
            var container = from.ContainerKey;
            foreach (var candidate in candidates)
            {
                if (files[candidate].ContainerKey == container)
                {
                    return candidate;
                }
            }
            return candidates[0];
        }

        private bool TryGetAssetsFile(out SerializedFile result)
        {
            result = null;
            if (m_FileID == 0)
            {
                result = assetsFile;
                return true;
            }

            if (m_FileID > 0 && m_FileID - 1 < assetsFile.m_Externals.Count)
            {
                var assetsManager = assetsFile.assetsManager;
                var assetsFileList = assetsManager.assetsFileList;
                var assetsFileIndexCache = assetsManager.assetsFileIndexCache;

                if (index == -2)
                {
                    var m_External = assetsFile.m_Externals[m_FileID - 1];
                    var name = m_External.fileName;
                    if (!assetsFileIndexCache.TryGetValue(name, out var candidates))
                    {
                        var found = new List<int>();
                        for (var i = 0; i < assetsFileList.Count; i++)
                        {
                            if (assetsFileList[i].fileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                            {
                                found.Add(i);
                            }
                        }
                        candidates = found.ToArray();
                        assetsFileIndexCache.TryAdd(name, candidates);
                    }
                    index = Pick(candidates, assetsFileList, assetsFile);
                }

                if (index >= 0)
                {
                    result = assetsFileList[index];
                    return true;
                }
            }

            return false;
        }

        public bool TryGet(out T result)
        {
            if (TryGetAssetsFile(out var sourceFile))
            {
                if (sourceFile.ObjectsDic.TryGetValue(m_PathID, out var obj))
                {
                    if (obj is T variable)
                    {
                        result = variable;
                        return true;
                    }
                }
            }

            result = null;
            return false;
        }

        public bool TryGet<T2>(out T2 result) where T2 : Object
        {
            if (TryGetAssetsFile(out var sourceFile))
            {
                if (sourceFile.ObjectsDic.TryGetValue(m_PathID, out var obj))
                {
                    if (obj is T2 variable)
                    {
                        result = variable;
                        return true;
                    }
                }
            }

            result = null;
            return false;
        }

        public void Set(T m_Object)
        {
            var name = m_Object.assetsFile.fileName;
            // Compare the files themselves, not their names: two loaded files can share a
            // CAB name, and treating the other one as "this file" retargets the pointer.
            if (ReferenceEquals(assetsFile, m_Object.assetsFile))
            {
                m_FileID = 0;
            }
            else
            {
                m_FileID = assetsFile.m_Externals.FindIndex(x => string.Equals(x.fileName, name, StringComparison.OrdinalIgnoreCase));
                if (m_FileID == -1)
                {
                    assetsFile.m_Externals.Add(new FileIdentifier
                    {
                        fileName = m_Object.assetsFile.fileName
                    });
                    m_FileID = assetsFile.m_Externals.Count;
                }
                else
                {
                    m_FileID += 1;
                }
            }

            var assetsManager = assetsFile.assetsManager;
            var assetsFileList = assetsManager.assetsFileList;
            var assetsFileIndexCache = assetsManager.assetsFileIndexCache;

            index = assetsFileList.IndexOf(m_Object.assetsFile);
            if (index < 0)
            {
                index = -1;
            }

            m_PathID = m_Object.m_PathID;
        }

        public PPtr<T2> Cast<T2>() where T2 : Object
        {
            return new PPtr<T2>(m_FileID, m_PathID, assetsFile);
        }

        public bool IsNull => m_PathID == 0 || m_FileID < 0;
    }
}
