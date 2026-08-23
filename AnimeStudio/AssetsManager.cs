using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static AnimeStudio.ImportHelper;

namespace AnimeStudio
{
    public class AssetsManager
    {
        public Game Game;
        public bool Silent = false;
        public bool SkipProcess = false;
        public bool ResolveDependencies = false;
        public string SpecifyUnityVersion;
        /// <summary>
        /// Invoked after each bundle/CAB group is loaded from a multi-bundle block.
        /// Used by map builders to process + release streams before the next bundle
        /// so peak RAM stays proportional to one bundle instead of the whole .block.
        /// </summary>
        public Action AfterBundleLoaded;

        /// <summary>Number of cached resource streams (for map-builder flush decisions).</summary>
        public int ResourceFileCount => resourceFileReaders.Count;
        public CancellationTokenSource tokenSource = new CancellationTokenSource();
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();

        internal Dictionary<string, int> assetsFileIndexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Concurrent: ResourceReader opens and registers a resource stream lazily, and that
        // can now happen from several threads at once while ReadAssets parses files in parallel.
        internal ConcurrentDictionary<string, BinaryReader> resourceFileReaders = new ConcurrentDictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);

        internal List<string> importFiles = new List<string>();
        internal HashSet<string> importFilesHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> noexistFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> assetsFileListHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public class AssetFilterDataItem
        {
            public String Source { get; set; }
            public ClassIDType Type { get; set; }
            public String Name { get; set; }
            public long PathID { get; set; }
            public long Offset { get; set; } = -1;
        }

        public class AssetFilterData
        {
            public List<AssetFilterDataItem> Items { get; set; }
        }

        public class AssetFilterDataItemEqualityComparer : IEqualityComparer<AssetFilterDataItem>
        {
            public bool Equals(AssetFilterDataItem? d1, AssetFilterDataItem? d2)
            {
                if (ReferenceEquals(d1, d2))
                    return true;

                if (d2 is null || d1 is null)
                    return false;

                return d1.Type == d2.Type && d1.PathID == d2.PathID && d1.Name.Equals(d2.Name, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(AssetFilterDataItem d) => HashCode.Combine(d.Name, d.PathID, d.Type);
        }

        public AssetFilterData FilterData = new AssetFilterData { Items = new List<AssetFilterDataItem>() };


        public void LoadFiles(params string[] files)
        {
            LoadFiles(files, mergeSplitAssets: true);
        }

        /// <param name="mergeSplitAssets">
        /// When false, skips <see cref="ImportHelper.MergeSplitAssets"/> / split-file filtering.
        /// Map builders already do that once up front; repeating it per file re-scans huge directories (HSR).
        /// </param>
        public void LoadFiles(string[] files, bool mergeSplitAssets)
        {
            if (Silent)
            {
                Logger.Silent = true;
                Progress.Silent = true;
            }

            string[] toReadFile;
            if (mergeSplitAssets)
            {
                var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
                MergeSplitAssets(path);
                toReadFile = ProcessingSplitFiles(files.ToList());
            }
            else
            {
                toReadFile = files;
            }

            if (ResolveDependencies)
                toReadFile = AssetsHelper.ProcessDependencies(toReadFile);
            Load(toReadFile);

            if (Silent)
            {
                Logger.Silent = false;
                Progress.Silent = false;
            }
        }

        public void LoadFolder(string path)
        {
            if (Silent)
            {
                Logger.Silent = true;
                Progress.Silent = true;
            }

            MergeSplitAssets(path, true);
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).ToList();
            var toReadFile = ProcessingSplitFiles(files);
            Load(toReadFile);

            if (Silent)
            {
                Logger.Silent = false;
                Progress.Silent = false;
            }
        }

        /// <summary>
        /// Everything one top-level input file produced, before it is merged into the manager.
        /// Loading writes here instead of straight into the shared collections, so independent
        /// input files can be read concurrently while the merge stays in input order and the
        /// result is identical to a serial run.
        /// </summary>
        private sealed class LoadBatch
        {
            public readonly List<SerializedFile> AssetsFiles = new List<SerializedFile>();
            public readonly List<KeyValuePair<string, BinaryReader>> ResourceFiles = new List<KeyValuePair<string, BinaryReader>>();
            /// <summary>External files discovered while resolving dependencies, loaded in the next wave.</summary>
            public readonly List<string> Dependencies = new List<string>();
            /// <summary>CAB names seen inside this input file, so duplicates are still skipped locally.</summary>
            public readonly HashSet<string> LocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Offsets requested by <see cref="FilterData"/> for the file being read.</summary>
            public List<long> Offsets;
            /// <summary>Names registered by an archive so dependency lookups skip them.</summary>
            public readonly List<string> RegisteredNames = new List<string>();
        }

        private void Load(string[] files)
        {
            foreach (var file in files)
            {
                Logger.Verbose($"caching {file} path and name to filter out duplicates");
                importFiles.Add(file);
                importFilesHash.Add(Path.GetFileName(file));
            }

            Progress.Reset();
            LoadImportFiles();

            importFiles.Clear();
            importFilesHash.Clear();
            noexistFiles.Clear();
            assetsFileListHash.Clear();
            AssetsHelper.ClearOffsets();

            if (!SkipProcess)
            {
                ReadAssets();
                ProcessAssets();
            }
        }

        /// <summary>
        /// Reads every queued input file. Files are independent -- each opens its own stream and
        /// decompresses into its own buffers -- so they are read concurrently and merged
        /// afterwards in input order. Dependency resolution can append new files while a wave
        /// runs, so the queue is drained in waves until nothing new appears.
        /// </summary>
        private void LoadImportFiles()
        {
            // Map builders hook AfterBundleLoaded to flush and release each bundle as it lands;
            // that only works if bundles arrive one at a time, so those runs stay serial.
            var degree = AfterBundleLoaded != null ? 1 : Math.Max(1, MaxParallelism);

            var processed = 0;
            var start = 0;
            while (start < importFiles.Count)
            {
                if (tokenSource.IsCancellationRequested)
                {
                    Logger.Info("Loading files has been aborted !!");
                    return;
                }

                var count = importFiles.Count - start;
                var wave = new string[count];
                importFiles.CopyTo(start, wave, 0, count);
                start = importFiles.Count;

                var batches = new LoadBatch[count];
                var total = importFiles.Count;

                if (degree == 1)
                {
                    for (int i = 0; i < count && !tokenSource.IsCancellationRequested; i++)
                    {
                        batches[i] = new LoadBatch();
                        LoadFile(wave[i], batches[i]);
                        Progress.Report(++processed, total);
                    }
                }
                else
                {
                    var options = new ParallelOptions { MaxDegreeOfParallelism = degree };
                    Parallel.For(0, count, options, (i, state) =>
                    {
                        if (tokenSource.IsCancellationRequested)
                        {
                            state.Stop();
                            return;
                        }
                        batches[i] = new LoadBatch();
                        LoadFile(wave[i], batches[i]);
                        Progress.Report(Interlocked.Increment(ref processed), total);
                    });
                }

                // Merging in wave order keeps assetsFileList, the index cache and the
                // first-wins duplicate rule exactly as the serial loop produced them.
                foreach (var batch in batches)
                {
                    if (batch != null)
                    {
                        Merge(batch);
                    }
                }
            }
        }

        private void Merge(LoadBatch batch)
        {
            foreach (var assetsFile in batch.AssetsFiles)
            {
                if (!assetsFileListHash.Add(assetsFile.fileName))
                {
                    // Another input file already contributed this CAB. First one wins, same as
                    // the serial loop did; release the duplicate stream instead of leaking it.
                    Logger.Info($"Skipping {assetsFile.originalPath} ({assetsFile.fileName})");
                    assetsFile.reader.Dispose();
                    continue;
                }
                assetsFileList.Add(assetsFile);
                assetsFileIndexCache.TryAdd(assetsFile.fileName, assetsFileList.Count - 1);
            }

            foreach (var resource in batch.ResourceFiles)
            {
                if (!resourceFileReaders.TryAdd(resource.Key, resource.Value))
                {
                    resource.Value.Dispose();
                }
            }

            foreach (var name in batch.RegisteredNames)
            {
                importFilesHash.Add(name);
            }

            foreach (var dependency in batch.Dependencies)
            {
                if (importFilesHash.Add(Path.GetFileName(dependency)))
                {
                    importFiles.Add(dependency);
                }
            }
        }

        private void LoadFile(string fullName, LoadBatch batch)
        {
            var reader = new FileReader(fullName);
            reader = reader.PreProcessing(Game);
            LoadFile(reader, batch);
        }

        private void LoadFile(FileReader reader, LoadBatch batch)
        {
            if (FilterData.Items.Count > 0)
            {
                var key = reader.FileName;
                var set = new HashSet<long>();

                foreach (var item in FilterData.Items)
                {
                    if (string.IsNullOrEmpty(item.Source))
                        continue;

                    var itemFileName = Path.GetFileName(item.Source);
                    if (!item.Source.Equals(key, StringComparison.OrdinalIgnoreCase) && !itemFileName.Equals(key, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (item.Offset >= 0)
                        set.Add(item.Offset);
                    else
                        if (AssetsHelper.TryGet(item.Source, out var offsets) && offsets.Length > 0)
                        foreach (var off in offsets)
                            set.Add(off);
                }

                batch.Offsets = set.ToList();
            }

            switch (reader.FileType)
            {
                case FileType.AssetsFile:
                    LoadAssetsFile(reader, batch);
                    break;
                case FileType.BundleFile:
                    LoadGameBlockFile(reader, batch);
                    break;
                case FileType.WebFile:
                    LoadWebFile(reader, batch);
                    break;
                case FileType.GZipFile:
                    LoadFile(DecompressGZip(reader), batch);
                    break;
                case FileType.BrotliFile:
                    LoadFile(DecompressBrotli(reader), batch);
                    break;
                case FileType.ZipFile:
                    LoadZipFile(reader, batch);
                    break;
                case FileType.BlockFile:
                case FileType.BlkFile:
                    LoadBlockFile(reader, batch);
                    break;
                case FileType.MhyFile:
                    LoadGameBlockFile(reader, batch);
                    break;
            }
        }

        private void LoadAssetsFile(FileReader reader, LoadBatch batch)
        {
            if (batch.LocalNames.Contains(reader.FileName))
            {
                Logger.Info($"Skipping {reader.FullPath}");
                reader.Dispose();
                return;
            }

            Logger.Info($"Loading {reader.FullPath}");
            try
            {
                var assetsFile = new SerializedFile(reader, this);
                CheckStrippedVersion(assetsFile);
                batch.AssetsFiles.Add(assetsFile);
                batch.LocalNames.Add(assetsFile.fileName);

                // External lookup does recursive Directory.GetFiles scans. Skip it when
                // dependencies are not being resolved (map builds, single-file loads) --
                // HSR-style CAB externals are almost never real on-disk files and the
                // repeated full-directory scans dominate both CPU and temporary allocations.
                if (ResolveDependencies)
                {
                    foreach (var sharedFile in assetsFile.m_Externals)
                    {
                        Logger.Verbose($"{assetsFile.fileName} needs external file {sharedFile.fileName}, attempting to look it up...");
                        var sharedFileName = sharedFile.fileName;
                        var directory = Path.GetDirectoryName(reader.FullPath);
                        var sharedFilePath = Path.Combine(directory, sharedFileName);

                        if (!File.Exists(sharedFilePath))
                        {
                            var findFiles = Directory.GetFiles(directory, sharedFileName, SearchOption.AllDirectories);
                            if (findFiles.Length > 0)
                            {
                                Logger.Verbose($"Found {findFiles.Length} matching files, picking first file {findFiles[0]} !!");
                                sharedFilePath = findFiles[0];
                            }
                        }
                        if (File.Exists(sharedFilePath))
                        {
                            // Queued for the next wave; the merge drops names already imported.
                            batch.Dependencies.Add(sharedFilePath);
                        }
                        else
                        {
                            Logger.Verbose("Nothing was found, dependency does not exist on disk");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading assets file {reader.FullPath}", e);
                reader.Dispose();
            }
        }

        private void LoadAssetsFromMemory(FileReader reader, LoadBatch batch, string originalPath, string unityVersion = null, long originalOffset = 0)
        {
            Logger.Verbose($"Loading asset file {reader.FileName} with version {unityVersion} from {originalPath} at offset 0x{originalOffset:X8}");
            if (!batch.LocalNames.Contains(reader.FileName))
            {
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    assetsFile.originalPath = originalPath;
                    assetsFile.offset = originalOffset;
                    if (!string.IsNullOrEmpty(unityVersion) && assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                    {
                        assetsFile.SetVersion(unityVersion);
                    }
                    CheckStrippedVersion(assetsFile);
                    batch.AssetsFiles.Add(assetsFile);
                    batch.LocalNames.Add(assetsFile.fileName);
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath} from {Path.GetFileName(originalPath)}", e);
                    // A file that failed to parse as a serialized file may still be a usable
                    // resource stream, so hand it to the merge instead of dropping it there.
                    batch.ResourceFiles.Add(new KeyValuePair<string, BinaryReader>(reader.FileName, reader));
                }
            }
            else
            {
                Logger.Info($"Skipping {originalPath} ({reader.FileName})");
                // Duplicate CAB name inside the same block -- the stream was freshly allocated
                // by BundleFile.ReadFiles and would otherwise leak until GC.
                reader.Dispose();
            }
        }

        private void LoadWebFile(FileReader reader, LoadBatch batch)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                var webFile = new WebFile(reader);
                foreach (var file in webFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    switch (subReader.FileType)
                    {
                        case FileType.AssetsFile:
                            LoadAssetsFromMemory(subReader, batch, reader.FullPath);
                            break;
                        case FileType.BundleFile:
                            LoadGameBlockFile(subReader, batch, reader.FullPath);
                            break;
                        case FileType.WebFile:
                            LoadWebFile(subReader, batch);
                            break;
                        case FileType.ResourceFile:
                            Logger.Verbose("Caching resource stream");
                            batch.ResourceFiles.Add(new KeyValuePair<string, BinaryReader>(file.fileName, subReader));
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading web file {reader.FullPath}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadZipFile(FileReader reader, LoadBatch batch)
        {
            Logger.Info("Loading " + reader.FileName);
            try
            {
                using (ZipArchive archive = new ZipArchive(reader.BaseStream, ZipArchiveMode.Read))
                {
                    List<string> splitFiles = new List<string>();
                    Logger.Verbose("Register all files before parsing the assets so that the external references can be found and find split files");
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Contains(".split"))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                            string basePath = Path.Combine(Path.GetDirectoryName(entry.FullName), baseName);
                            if (!splitFiles.Contains(basePath))
                            {
                                splitFiles.Add(basePath);
                                batch.RegisteredNames.Add(baseName);
                            }
                        }
                        else
                        {
                            batch.RegisteredNames.Add(entry.Name);
                        }
                    }

                    Logger.Verbose("Merge split files and load the result");
                    foreach (string basePath in splitFiles)
                    {
                        try
                        {
                            Stream splitStream = new MemoryStream();
                            int i = 0;
                            while (true)
                            {
                                string path = $"{basePath}.split{i++}";
                                ZipArchiveEntry entry = archive.GetEntry(path);
                                if (entry == null)
                                    break;
                                using (Stream entryStream = entry.Open())
                                {
                                    entryStream.CopyTo(splitStream);
                                }
                            }
                            splitStream.Seek(0, SeekOrigin.Begin);
                            FileReader entryReader = new FileReader(basePath, splitStream);
                            entryReader = entryReader.PreProcessing(Game);
                            LoadFile(entryReader, batch);
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip split file {basePath}", e);
                        }
                    }

                    Logger.Verbose("Load all entries");
                    Logger.Verbose($"Found {archive.Entries.Count} entries"); 
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        try
                        {
                            string dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), reader.FileName, entry.FullName);
                            Logger.Verbose("Create a new stream to store the deflated stream in and keep the data for later extraction");
                            Stream streamReader = new MemoryStream();
                            using (Stream entryStream = entry.Open())
                            {
                                entryStream.CopyTo(streamReader);
                            }
                            streamReader.Position = 0;

                            FileReader entryReader = new FileReader(dummyPath, streamReader);
                            entryReader = entryReader.PreProcessing(Game);
                            LoadFile(entryReader, batch);
                            if (entryReader.FileType == FileType.ResourceFile)
                            {
                                entryReader.Position = 0;
                                Logger.Verbose("Caching resource file");
                                batch.ResourceFiles.Add(new KeyValuePair<string, BinaryReader>(entry.Name, entryReader));
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip entry {entry.FullName}", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading zip file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadBlockFile(FileReader reader, LoadBatch batch)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                // XORStream derives from OffsetStream, so both branches share a static type
                // and neither GetOffsets nor Length needs dynamic dispatch.
                OffsetStream stream;

                switch (reader.FileType)
                {
                    case FileType.BlkFile:
                        stream = BlkUtils.Decrypt(reader, (Blk)Game);
                        break;
                    default:
                        stream = new OffsetStream(reader.BaseStream, 0);
                        break;
                }

                Progress.Reset();
                using (stream)
                {
                    var total = stream.Length;

                    var manualOffsets = batch.Offsets;
                    bool isManualOffsets = (manualOffsets != null && manualOffsets.Count > 0) && Game.Type.IsArknightsEndfieldGroup();
                    IEnumerable<long> offsetsEnumerable = isManualOffsets
                        ? manualOffsets
                        : stream.GetOffsets(reader.FullPath);

                    int idx = 0;
                    int? manualTotal = (manualOffsets != null && manualOffsets.Count > 0) ? manualOffsets.Count : (int?)null;
                    foreach (var offset in offsetsEnumerable)
                    {
                        var name = offset.ToString("X8");
                        Logger.Verbose($"Loading Block {name}");

                        var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), name);
                        var subReader = new FileReader(dummyPath, stream, true);
                        if (isManualOffsets)
                            subReader.Position = offset;
                        LoadGameBlockFile(subReader, batch, reader.FullPath, offset, false);

                        if (manualTotal.HasValue)
                            Progress.Report(idx + 1, manualTotal.Value);
                        else
                            Progress.Report((int)offset, (int)total);
                        idx++;
                    }
                }
                
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading block file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }
        private void LoadGameBlockFile(FileReader reader, LoadBatch batch, string originalPath = null, long originalOffset = 0, bool log = true)
        {
            if (log)
            {
                Logger.Info("Loading " + reader.FullPath);
            }
            try
            {
                IBundleContainer file = null;

                switch (reader.FileType)
                {
                    case FileType.ENCRFile:
                    case FileType.BundleFile:
                        file = new BundleFile(reader, Game);
                        break;
                    case FileType.Blb3File:
                        file = new Blb3File(reader, reader.FullPath);
                        break;
                    case FileType.MhyFile:
                        file = new MhyFile(reader, (Mhy)Game);
                        break;
                    case FileType.HygFile:
                        file = new HygFile(reader, reader.FullPath);
                        break;
                    case FileType.VFSFile:
                        file = new VFSFile(reader, reader.FullPath, Game.Type);
                        break;
                }

                if (file == null)
                    throw new Exception("Unsupported game block file type");

                Logger.Verbose($"file total size: {file.Header.size:X8}");
                foreach (var innerFile in file.Files)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), innerFile.fileName);
                    var cabReader = new FileReader(dummyPath, innerFile.stream);
                    if (cabReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(cabReader, batch, originalPath ?? reader.FullPath, file.Header.unityRevision, originalOffset);
                    }
                    else
                    {
                        Logger.Verbose("Caching resource stream");
                        // The merge disposes the loser on a name collision, so no stream is
                        // left without an owner.
                        batch.ResourceFiles.Add(new KeyValuePair<string, BinaryReader>(innerFile.fileName, cabReader));
                    }
                }
            }
            catch (InvalidCastException)
            {
                string name = "";
                switch (reader.FileType)
                {
                    case FileType.ENCRFile:
                    case FileType.BundleFile:
                        name = nameof(Mr0k);
                        break;
                    case FileType.Blb3File:
                        name = nameof(Blb3File);
                        break;
                    case FileType.MhyFile:
                        name = nameof(Mhy);
                        break;
                    case FileType.HygFile:
                        name = nameof(HygFile);
                        break;
                    case FileType.VFSFile:
                        name = nameof(VFSFile);
                        break;
                }
                Logger.Error($"Game type mismatch, Expected {name} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            catch (Exception e)
            {
                var str = $"Error while reading file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
                // Notify map builders after each bundle so they can flush entries and free streams.
                AfterBundleLoaded?.Invoke();
            }
        }

        public void CheckStrippedVersion(SerializedFile assetsFile)
        {
            if(Game.Type.IsAzurPromiliaCBT2() && assetsFile.IsVersionStripped) SpecifyUnityVersion = "2022.3.62f3";
            if (assetsFile.IsVersionStripped && string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                throw new Exception("The Unity version has been stripped, please set the version in the options");
            }
            if (!string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                assetsFile.SetVersion(SpecifyUnityVersion);
            }
        }

        /// <summary>
        /// Dispose loaded asset/resource streams but keep <see cref="assetsFileListHash"/>
        /// so subsequent bundles in the same block still skip already-seen CAB names.
        /// </summary>
        public void ClearLoadedAssets()
        {
            Logger.Verbose("Cleaning loaded assets...");

            foreach (var assetsFile in assetsFileList)
            {
                assetsFile.Objects.Clear();
                assetsFile.ObjectsDic.Clear();
                assetsFile.reader.Close();
            }
            assetsFileList.Clear();

            foreach (var resourceFileReader in resourceFileReaders)
            {
                resourceFileReader.Value.Close();
            }
            resourceFileReaders.Clear();

            assetsFileIndexCache.Clear();
        }

        public void Clear()
        {
            Logger.Verbose("Cleaning up...");

            ClearLoadedAssets();

            assetsFileListHash.Clear();

            tokenSource.Dispose();
            tokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Degree of parallelism for the independent-per-file stages of loading.
        /// One thread per core; a value of 1 restores the fully serial behaviour.
        /// </summary>
        public static int MaxParallelism { get; set; } = Environment.ProcessorCount;

        private void ReadAssets()
        {
            Logger.Info("Read assets...");

            var progressCount = 0;
            foreach (var assetsFile in assetsFileList)
                progressCount += assetsFile.m_Objects.Count;

            var done = 0;
            Progress.Reset();

            // Every serialized file owns its reader and its own object collections, and object
            // constructors only read from the file they belong to -- no cross-file lookups happen
            // until ProcessAssets. So files can be parsed concurrently while objects inside a file
            // stay strictly in order, which keeps the result identical to the serial version.
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism) };
            Parallel.ForEach(assetsFileList, options, (assetsFile, state) =>
            {
                if (tokenSource.IsCancellationRequested)
                {
                    state.Stop();
                    return;
                }

                foreach (var objectInfo in assetsFile.m_Objects)
                {
                    if (tokenSource.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }

                    ReadObject(assetsFile, objectInfo);

                    var current = Interlocked.Increment(ref done);
                    if ((current & 0x3FF) == 0)
                        Progress.Report(current, progressCount);
                }
            });

            if (tokenSource.IsCancellationRequested)
            {
                Logger.Info("Reading assets has been cancelled !!");
                return;
            }
            Progress.Report(progressCount, progressCount);
        }

        private void ReadObject(SerializedFile assetsFile, ObjectInfo objectInfo)
        {
            var objectReader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo, Game);
            try
            {
                Object obj = objectReader.type switch
                {
                    ClassIDType.Animation when ClassIDType.Animation.CanParse() => new Animation(objectReader),
                    ClassIDType.AnimationClip when ClassIDType.AnimationClip.CanParse() => new AnimationClip(objectReader),
                    ClassIDType.Animator when ClassIDType.Animator.CanParse() => new Animator(objectReader),
                    ClassIDType.AnimatorController when ClassIDType.AnimatorController.CanParse() => new AnimatorController(objectReader),
                    ClassIDType.AnimatorOverrideController when ClassIDType.AnimatorOverrideController.CanParse() => new AnimatorOverrideController(objectReader),
                    ClassIDType.AssetBundle when ClassIDType.AssetBundle.CanParse() => new AssetBundle(objectReader),
                    ClassIDType.AudioClip when ClassIDType.AudioClip.CanParse() => new AudioClip(objectReader),
                    ClassIDType.Avatar when ClassIDType.Avatar.CanParse() => new Avatar(objectReader),
                    ClassIDType.Font when ClassIDType.Font.CanParse() => new Font(objectReader),
                    ClassIDType.GameObject when ClassIDType.GameObject.CanParse() => new GameObject(objectReader),
                    ClassIDType.IndexObject when ClassIDType.IndexObject.CanParse() => new IndexObject(objectReader),
                    ClassIDType.Material when ClassIDType.Material.CanParse() => new Material(objectReader),
                    ClassIDType.Mesh when ClassIDType.Mesh.CanParse() => new Mesh(objectReader),
                    ClassIDType.MeshFilter when ClassIDType.MeshFilter.CanParse() => new MeshFilter(objectReader),
                    ClassIDType.MeshRenderer when ClassIDType.MeshRenderer.CanParse() => new MeshRenderer(objectReader),
                    ClassIDType.MiHoYoBinData when ClassIDType.MiHoYoBinData.CanParse() => new MiHoYoBinData(objectReader),
                    ClassIDType.MonoBehaviour when ClassIDType.MonoBehaviour.CanParse() => new MonoBehaviour(objectReader),
                    ClassIDType.MonoScript when ClassIDType.MonoScript.CanParse() => new MonoScript(objectReader),
                    ClassIDType.MovieTexture when ClassIDType.MovieTexture.CanParse() => new MovieTexture(objectReader),
                    ClassIDType.PlayerSettings when ClassIDType.PlayerSettings.CanParse() => new PlayerSettings(objectReader),
                    ClassIDType.RectTransform when ClassIDType.RectTransform.CanParse() => new RectTransform(objectReader),
                    ClassIDType.Shader when ClassIDType.Shader.CanParse() => new Shader(objectReader),
                    ClassIDType.SkinnedMeshRenderer when ClassIDType.SkinnedMeshRenderer.CanParse() => new SkinnedMeshRenderer(objectReader),
                    ClassIDType.Sprite when ClassIDType.Sprite.CanParse() => new Sprite(objectReader),
                    ClassIDType.SpriteAtlas when ClassIDType.SpriteAtlas.CanParse() => new SpriteAtlas(objectReader),
                    ClassIDType.TextAsset when ClassIDType.TextAsset.CanParse() => new TextAsset(objectReader),
                    ClassIDType.Texture2D when ClassIDType.Texture2D.CanParse() => new Texture2D(objectReader),
                    ClassIDType.Transform when ClassIDType.Transform.CanParse() => new Transform(objectReader),
                    ClassIDType.VideoClip when ClassIDType.VideoClip.CanParse() => new VideoClip(objectReader),
                    ClassIDType.ResourceManager when ClassIDType.ResourceManager.CanParse() => new ResourceManager(objectReader),
                    ClassIDType.NapAssetBundleIndexAsset when ClassIDType.NapAssetBundleIndexAsset.CanParse() => new NapAssetBundleIndexAsset(objectReader),
                    _ => new Object(objectReader),
                };
                assetsFile.AddObject(obj);
            }
            catch (Exception e)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Unable to load object")
                    .AppendLine($"Assets {assetsFile.fileName}")
                    .AppendLine($"Path {assetsFile.originalPath}")
                    .AppendLine($"Type {objectReader.type}")
                    .AppendLine($"PathID {objectInfo.m_PathID}")
                    .Append(e);
                Logger.Error(sb.ToString());
            }
        }

        private void ProcessAssets()
        {
            Logger.Info("Process Assets...");

            var separateMeshes = new Dictionary<string, PPtr<Mesh>>();
            var avatars = new List<GameObject>();
            var fileID = 0;

            if (Game.Type.IsZZZGroup())
            {   
                // TODO: Refactor this to decrease the number of meshes. Possibly do this after we build the hierarchy to discover unused meshes (which are likely to be SeparateMeshes...)
                // TODO: Somehow RE the behavior used to swap meshes to determine exact mappings instead of guessing by name...
                foreach (var assetsFile in assetsFileList)
                {
                    foreach (var obj in assetsFile.Objects)
                    {
                        if (tokenSource.IsCancellationRequested)
                        {
                            Logger.Info("Processing assets has been cancelled !!");
                            return;
                        }
                        if (obj.type == ClassIDType.Mesh)
                        {
                            var pptr = new PPtr<Mesh>(0, obj.m_PathID, assetsFile);
                            if (pptr.TryGet(out var mesh))
                            {
                                if (separateMeshes.ContainsKey(obj.Name))
                                {
                                    // Logger.Warning($"Found possible duplicate SeparateMesh: {obj.Name}, {mesh.Name}");
                                }
                                else
                                {
                                    Logger.Verbose($"Found SeparateMesh {mesh.Name}");
                                    separateMeshes.Add(obj.Name, pptr);
                                }
                            }
                            else
                            {
                                throw new Exception($"Invalid PPtr for {obj.Name}");
                            }
                        }
                    }
                    fileID++;
                }
                Logger.Info($"Found {separateMeshes.Count} SeparateMeshes");
            }
            
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (tokenSource.IsCancellationRequested)
                    {
                        Logger.Info("Processing assets has been cancelled !!");
                        return;
                    }
                    if (obj is GameObject m_GameObject)
                    {
                        Logger.Verbose($"GameObject with {m_GameObject.m_PathID} in file {m_GameObject.assetsFile.fileName} has {m_GameObject.m_Components.Count} components, Attempting to fetch them...");
                        foreach (var pptr in m_GameObject.m_Components)
                        {
                            if (pptr.TryGet(out var m_Component))
                            {
                                switch (m_Component)
                                {
                                    case Transform m_Transform:
                                        Logger.Verbose($"Fetched Transform component with {m_Transform.m_PathID} in file {m_Transform.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_Transform = m_Transform;
                                            break;
                                    case MeshRenderer m_MeshRenderer:
                                        Logger.Verbose($"Fetched MeshRenderer component with {m_MeshRenderer.m_PathID} in file {m_MeshRenderer.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_MeshRenderer = m_MeshRenderer;
                                            break;
                                    case MeshFilter m_MeshFilter:
                                        Logger.Verbose($"Fetched MeshFilter component with {m_MeshFilter.m_PathID} in file {m_MeshFilter.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_MeshFilter = m_MeshFilter;
                                            break;
                                    case SkinnedMeshRenderer m_SkinnedMeshRenderer:
                                        Logger.Verbose($"Fetched SkinnedMeshRenderer component with {m_SkinnedMeshRenderer.m_PathID} in file {m_SkinnedMeshRenderer.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_SkinnedMeshRenderer = m_SkinnedMeshRenderer;
                                            break;
                                    case Animator m_Animator:
                                        Logger.Verbose($"Fetched Animator component with {m_Animator.m_PathID} in file {m_Animator.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_Animator = m_Animator;
                                            break;
                                    case Animation m_Animation:
                                        Logger.Verbose($"Fetched Animation component with {m_Animation.m_PathID} in file {m_Animation.assetsFile.fileName}, assigning to GameObject components...");
                                        m_GameObject.m_Animation = m_Animation;
                                            break;
                                }
                            }
                        }
                        // Ordinal: these are fixed ASCII markers in asset names, and the default
                        // culture-sensitive overloads went through ICU for every GameObject in
                        // the load -- 5.8% of load CPU on this single line.
                        if (Game.Type.IsZZZGroup())
                        {
                            var goName = m_GameObject.Name;
                            if (m_GameObject.m_Animator != null
                                || goName.StartsWith("Avatar_", StringComparison.Ordinal)
                                || goName.EndsWith("_Model", StringComparison.Ordinal))
                            {
                                avatars.Add(m_GameObject);
                            }
                        }
                    }
                    else if (obj is SpriteAtlas m_SpriteAtlas)
                    {
                        if (m_SpriteAtlas.m_RenderDataMap.Count > 0)
                        {
                            Logger.Verbose($"SpriteAtlas with {m_SpriteAtlas.m_PathID} in file {m_SpriteAtlas.assetsFile.fileName} has {m_SpriteAtlas.m_PackedSprites.Count} packed sprites, Attempting to fetch them...");
                            foreach (var m_PackedSprite in m_SpriteAtlas.m_PackedSprites)
                            {
                                if (m_PackedSprite.TryGet(out var m_Sprite))
                                {
                                    if (m_Sprite.m_SpriteAtlas.IsNull)
                                    {
                                        Logger.Verbose($"Fetched Sprite with {m_Sprite.m_PathID} in file {m_Sprite.assetsFile.fileName}, assigning to parent SpriteAtlas...");
                                        m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                    }
                                    else
                                    {
                                        m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlaOld);
                                        if (m_SpriteAtlaOld.m_IsVariant)
                                        {
                                            Logger.Verbose($"Fetched Sprite with {m_Sprite.m_PathID} in file {m_Sprite.assetsFile.fileName} has a variant of the origianl SpriteAtlas, disposing of the variant and assinging to the parent SpriteAtlas...");
                                            m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (Game.Type.IsZZZGroup())
            {
                Logger.Info($"Found {avatars.Count} Avatars");

                // Per-strategy tallies so the two routes can be compared on real data.
                var attachedBy = new int[3];
                var unattached = 0;

                foreach (var avatar in avatars)
                {
                    var rootName = avatar.Name;
                    Logger.Verbose($"Attempting to process SeparateMesh for {rootName}");

                    if (avatar.m_Transform == null)
                    {
                        continue;
                    }

                    foreach (var childPtr in avatar.m_Transform.m_Children)
                    {
                        if (!childPtr.TryGet(out var child) || !child.m_GameObject.TryGet(out var childGO))
                        {
                            continue;
                        }

                        // Two strategies exist for naming a ZZZ separate mesh and neither covers
                        // every asset: the NapLodController component records the real asset path
                        // (precise, but the component is not always present), while _Model bundles
                        // follow the SeparateMesh_<avatar>_<child> convention. Try them in order of
                        // precision and stop at the first one that attaches.
                        var attached = false;
                        foreach (var (candidate, strategy) in SeparateMeshCandidates(childGO, rootName))
                        {
                            if (TryAttachSeparateMesh(childGO, candidate, separateMeshes))
                            {
                                attachedBy[strategy]++;
                                attached = true;
                                break;
                            }
                        }

                        // Only count renderers that are still missing a mesh. A child whose mesh
                        // was already assigned needs no separate mesh and is not a failure.
                        if (!attached
                            && ((childGO.m_SkinnedMeshRenderer != null && childGO.m_SkinnedMeshRenderer.m_Mesh.IsNull)
                                || (childGO.m_MeshFilter != null && childGO.m_MeshFilter.m_Mesh.IsNull)))
                        {
                            unattached++;
                        }
                    }
                }

                Logger.Info($"SeparateMesh attached: {attachedBy[0]} via NapLodController, {attachedBy[1]} via name scheme, {attachedBy[2]} via child name; {unattached} renderers left without a mesh");
            }
        }

        /// <summary>
        /// Mesh names to try for a child of a ZZZ avatar, most specific first, each tagged with
        /// the strategy that produced it (0 = NapLodController, 1 = name scheme, 2 = child name).
        /// </summary>
        private static IEnumerable<(string Name, int Strategy)> SeparateMeshCandidates(GameObject childGO, string rootName)
        {
            var childName = childGO.Name;

            foreach (var component in childGO.m_Components)
            {
                if (!component.TryGet<MonoBehaviour>(out var comp) || comp.Name != "NapLodController")
                {
                    continue;
                }

                var name = MeshNameFromNapLodController(comp);
                if (!string.IsNullOrEmpty(name))
                {
                    yield return (name, 0);
                }
            }

            yield return ("SeparateMesh_" + rootName + "_" + childName, 1);
            yield return (childName, 2);
        }

        /// <summary>
        /// Extracts the mesh name out of a NapLodController's raw asset path.
        /// Returns null when the component does not carry a usable path.
        /// </summary>
        private static string MeshNameFromNapLodController(MonoBehaviour comp)
        {
            var raw = comp.GetRawData();
            var path = raw != null ? System.Text.Encoding.UTF8.GetString(raw) : string.Empty;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var assetIndex = path.IndexOf("Assets", StringComparison.Ordinal);
            string trimmed;
            if (assetIndex != -1)
            {
                trimmed = path.Substring(assetIndex);
            }
            else if (path.Length > 40)
            {
                trimmed = path.Substring(40);
            }
            else
            {
                Logger.Verbose($"NapLodController path too short ({path.Length}), skipping");
                return null;
            }

            trimmed = trimmed?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            var meshIndex = trimmed.IndexOf(".mesh", StringComparison.OrdinalIgnoreCase);
            if (meshIndex <= 0)
            {
                Logger.Verbose($".mesh not found in '{trimmed}', skipping");
                return null;
            }

            trimmed = trimmed.Substring(0, meshIndex);

            var lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < trimmed.Length - 1)
            {
                trimmed = trimmed.Substring(lastSlash + 1);
            }

            trimmed = trimmed.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        /// <summary>
        /// Attaches a separate mesh to the child's renderer if one is registered under that name
        /// and the renderer has no mesh yet. Returns whether an attachment happened.
        /// </summary>
        private static bool TryAttachSeparateMesh(GameObject childGO, string meshName, Dictionary<string, PPtr<Mesh>> separateMeshes)
        {
            if (string.IsNullOrEmpty(meshName) || !separateMeshes.TryGetValue(meshName, out var meshPPtr))
            {
                return false;
            }

            Logger.Verbose($"Trying to attach {meshName} to {childGO.Name}");

            if (childGO.m_SkinnedMeshRenderer != null && childGO.m_SkinnedMeshRenderer.m_Mesh.IsNull)
            {
                Logger.Info($"Attached {meshName} to {childGO.Name}");
                childGO.m_SkinnedMeshRenderer.m_Mesh = meshPPtr;
                return true;
            }

            if (childGO.m_MeshFilter != null && childGO.m_MeshFilter.m_Mesh.IsNull)
            {
                Logger.Info($"Attached {meshName} to {childGO.Name}");
                childGO.m_MeshFilter.m_Mesh = meshPPtr;
                return true;
            }

            return false;
        }
    }
}
