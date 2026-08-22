using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MessagePack;
using MessagePack.Formatters;
using Newtonsoft.Json;

namespace AnimeStudio
{
    public static class StringCache
    {
        private static readonly HashSet<string> _cache = new(StringComparer.Ordinal);

        public static string Get(string value)
        {
            if (value == null) return null;

            if (_cache.TryGetValue(value, out var cached))
                return cached;

            _cache.Add(value);
            return value;
        }

        /// <summary>
        /// Drop interned strings. Call between map-build files so unique asset names
        /// from already-flushed entries do not accumulate across an entire game dump.
        /// </summary>
        public static void Clear()
        {
            _cache.Clear();
        }

        public static int Count => _cache.Count;
    }

    [MessagePackObject]
    public record AssetMap
    {
        [Key(0)]
        public GameType GameType { get; set; }

        [Key(1)]
        public List<AssetEntry> AssetEntries { get; set; }
    }

    /// <summary>
    /// Entries are written as a 7 element MessagePack array (keys 0-6). Older maps built by
    /// the acl_fix branch only carry 6 elements (no <see cref="Offset"/>); <see cref="AssetEntryFormatter"/>
    /// reads both and always writes 7, so both map generations stay loadable.
    /// </summary>
    [MessagePackObject]
    [MessagePackFormatter(typeof(AssetEntryFormatter))]
    public partial record AssetEntry
    {
        private string _container;
        private string _hash;
        private string _name;
        private string _source;

        // Names are usually unique per asset — interning them only grows the cache.
        [Key(0)]
        public string Name {
            get => _name;
            set => _name = value;
        }

        // Containers and sources repeat heavily across entries; keep interning those.
        [Key(1)]
        public string Container {
            get => _container;
            set => _container = StringCache.Get(value);
        }

        [Key(2)]
        public string Source {
            get => _source;
            set => _source = StringCache.Get(value);
        }

        [Key(3)]
        public long PathID { get; set; }

        [Key(4)]
        public ClassIDType Type { get; set; }

        // Hash is effectively unique per asset — interning it only grows the cache without reuse.
        // Opaque string: XXH64 hex (or "size:<hex>") in maps built here, SHA-256 hex in maps
        // built by the acl_fix branch. Never converted, only compared.
        [Key(5)]
        public string Hash {
            get => _hash;
            set => _hash = value;
        }

        /// <summary>
        /// Read-only alias so a .json map written by the acl_fix branch (field name "SHA256Hash")
        /// deserializes into <see cref="Hash"/>. Not written back out.
        /// </summary>
        [IgnoreMember]
        [JsonProperty("SHA256Hash")]
        public string SHA256Hash {
            get => _hash;
            set => _hash ??= value;
        }

        public bool ShouldSerializeSHA256Hash() => false;

        // -1 means "unknown" (map predates this field) and makes AssetsManager fall back to
        // the CABMap offsets. Must never default to 0 — that would silently read the wrong bundle.
        [Key(6)]
        public long Offset { get; set; } = -1;

        private string GetPropertyValue(string propertyName)
        {
            return propertyName switch
            {
                    nameof(Name)      => Name,
                    nameof(Container) => Container,
                    nameof(Source)    => Source,
                    nameof(PathID)    => PathID.ToString(),
                    nameof(Type)      => Type.ToString(),
                    nameof(Hash)      => Hash ?? string.Empty,
                    "SHA256Hash"      => Hash ?? string.Empty,
                    _                 => null
            };
        }

        public bool Matches(Dictionary<string, Regex> filters)
        {
            if(filters is null || filters.Count == 0)
                return true;

            foreach ((string key, Regex regex) in filters)
            {
                string value = this.GetPropertyValue(key);
                if(value is null || !regex.IsMatch(value))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Explicit MessagePack formatter for <see cref="AssetEntry"/>.
    /// Reads both the legacy 6 element array (acl_fix branch, no Offset) and the current
    /// 7 element array, and always writes 7 elements. A missing key 6 yields
    /// <c>Offset = -1</c>, which is what <c>AssetsManager</c> checks (<c>item.Offset &gt;= 0</c>)
    /// before falling back to the CABMap offsets — 0 would be taken as a real offset.
    /// </summary>
    public sealed class AssetEntryFormatter : IMessagePackFormatter<AssetEntry>
    {
        public AssetEntry Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
                return null;

            options.Security.DepthStep(ref reader);
            var count = reader.ReadArrayHeader();
            var entry = new AssetEntry { Offset = -1 };
            for (var i = 0; i < count; i++)
            {
                switch (i)
                {
                    case 0: entry.Name = reader.ReadString(); break;
                    case 1: entry.Container = reader.ReadString(); break;
                    case 2: entry.Source = reader.ReadString(); break;
                    case 3: entry.PathID = reader.ReadInt64(); break;
                    case 4: entry.Type = (ClassIDType)reader.ReadInt32(); break;
                    case 5: entry.Hash = reader.ReadString(); break;
                    case 6: entry.Offset = reader.ReadInt64(); break;
                    default: reader.Skip(); break;
                }
            }
            reader.Depth--;
            return entry;
        }

        public void Serialize(ref MessagePackWriter writer, AssetEntry value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            writer.WriteArrayHeader(7);
            writer.Write(value.Name);
            writer.Write(value.Container);
            writer.Write(value.Source);
            writer.Write(value.PathID);
            writer.Write((int)value.Type);
            writer.Write(value.Hash);
            writer.Write(value.Offset);
        }
    }
}
