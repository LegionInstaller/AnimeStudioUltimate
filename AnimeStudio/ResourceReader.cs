using System;
using System.Collections.Concurrent;
using System.IO;

namespace AnimeStudio
{
    public class ResourceReader
    {
        private bool needSearch;
        private string path;
        private SerializedFile assetsFile;
        private long offset;
        private long size;
        private BinaryReader reader;

        public int Size { get => (int)size; }

        public ResourceReader(string path, SerializedFile assetsFile, long offset, long size)
        {
            needSearch = true;
            this.path = path;
            this.assetsFile = assetsFile;
            this.offset = offset;
            this.size = size;
        }

        public ResourceReader(BinaryReader reader, long offset, long size)
        {
            this.reader = reader;
            this.offset = offset;
            this.size = size;
        }

        // A resource file that is not on disk stays not on disk. Without this, every asset
        // referencing the same missing .resS repeated the recursive Directory.GetFiles scan
        // below -- on a ZZZ data folder that is a walk over ~9800 block files per lookup.
        private static readonly ConcurrentDictionary<string, byte> missingResourceFiles =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private BinaryReader GetReader()
        {
            if (needSearch)
            {
                var resourceFileName = Path.GetFileName(path);
                var manager = assetsFile.assetsManager;
                // Prefer the stream that came from this file's own container. ZZZ names a
                // .resS after its CAB, and two containers can ship the same name with
                // different bytes -- taking either one would read plausible garbage.
                if (manager.resourceFileReaders.TryGetValue(
                        AssetsManager.ResourceKey(assetsFile.ContainerKey, resourceFileName), out reader)
                    || manager.resourceFileReadersByName.TryGetValue(resourceFileName, out reader))
                {
                    needSearch = false;
                    return reader;
                }
                var assetsFileDirectory = Path.GetDirectoryName(assetsFile.fullName);
                var resourceFilePath = Path.Combine(assetsFileDirectory, resourceFileName);
                var searchKey = assetsFileDirectory + "\0" + resourceFileName;
                if (missingResourceFiles.ContainsKey(searchKey))
                {
                    throw new FileNotFoundException($"Can't find the resource file {resourceFileName}");
                }
                if (!File.Exists(resourceFilePath))
                {
                    var findFiles = Directory.GetFiles(assetsFileDirectory, resourceFileName, SearchOption.AllDirectories);
                    if (findFiles.Length > 0)
                    {
                        resourceFilePath = findFiles[0];
                    }
                }
                if (File.Exists(resourceFilePath))
                {
                    var opened = new BinaryReader(File.OpenRead(resourceFilePath));
                    // Another thread may have registered the same file first; use the winner
                    // so every caller shares one stream, and close the loser.
                    var key = AssetsManager.ResourceKey(assetsFile.ContainerKey, resourceFileName);
                    if (manager.resourceFileReaders.TryAdd(key, opened))
                    {
                        reader = opened;
                        manager.resourceFileReadersByName.TryAdd(resourceFileName, opened);
                    }
                    else
                    {
                        opened.Dispose();
                        reader = manager.resourceFileReaders[key];
                    }
                    needSearch = false;
                    return reader;
                }
                missingResourceFiles.TryAdd(searchKey, 0);
                throw new FileNotFoundException($"Can't find the resource file {resourceFileName}");
            }
            else
            {
                return reader;
            }
        }

        // Seek and read have to happen together. One resource stream is shared by every asset
        // that points into the same .resS -- across serialized file boundaries, so splitting the
        // work per file does not separate them. Locking the reader itself keeps the contention
        // per resource file rather than global, and the expensive part of an export (decoding,
        // encoding) happens outside these methods.

        public byte[] GetData()
        {
            var binaryReader = GetReader();
            lock (binaryReader)
            {
                binaryReader.BaseStream.Position = offset;
                return binaryReader.ReadBytes((int)size);
            }
        }

        public void GetData(byte[] buff)
        {
            var binaryReader = GetReader();
            lock (binaryReader)
            {
                binaryReader.BaseStream.Position = offset;
                binaryReader.Read(buff, 0, (int)size);
            }
        }

        public void WriteData(string path)
        {
            var binaryReader = GetReader();
            using (var writer = File.OpenWrite(path))
            {
                lock (binaryReader)
                {
                    binaryReader.BaseStream.Position = offset;
                    binaryReader.BaseStream.CopyTo(writer, size);
                }
            }
        }
    }
}
