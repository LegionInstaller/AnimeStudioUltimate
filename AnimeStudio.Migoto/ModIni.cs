using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio.Migoto
{
    /// <summary>
    /// One entry of an ini section. Keys repeat -- a section can hold several
    /// <c>drawindexed</c> lines -- so the entries stay an ordered list rather than a map,
    /// and each remembers the <c>if</c> it sat inside.
    /// </summary>
    public sealed class IniEntry
    {
        public string Key { get; }
        public string Value { get; }

        /// <summary>The condition of the enclosing <c>if</c>, or null at section level.</summary>
        public string Condition { get; }

        public IniEntry(string key, string value, string condition)
        {
            Key = key;
            Value = value;
            Condition = condition;
        }

        public override string ToString() =>
            Condition == null ? $"{Key} = {Value}" : $"[{Condition}] {Key} = {Value}";
    }

    public sealed class IniSection
    {
        public string Name { get; }
        public List<IniEntry> Entries { get; } = new List<IniEntry>();

        public IniSection(string name) => Name = name;

        /// <summary>The first value for <paramref name="key"/>, or null.</summary>
        public string Get(string key)
        {
            foreach (var e in Entries)
            {
                if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                    return e.Value;
            }
            return null;
        }

        public IEnumerable<IniEntry> All(string key)
        {
            foreach (var e in Entries)
            {
                if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                    yield return e;
            }
        }

        public int GetInt(string key, int fallback = -1) =>
            int.TryParse(Get(key), out var value) ? value : fallback;

        public override string ToString() => $"[{Name}] {Entries.Count} entries";
    }

    /// <summary>
    /// A 3DMigoto ini, read literally -- sections, ordered entries, and the conditions of
    /// the <c>if</c> blocks. Nothing is interpreted here; <see cref="MigotoMod"/> does that.
    ///
    /// The dialect is small: <c>[Section]</c> headers, <c>key = value</c>, <c>;</c> comments,
    /// and <c>if</c>/<c>else</c>/<c>endif</c> around draw calls. Namespaces such as
    /// <c>Resource\ZZMI\Diffuse</c> are ordinary keys and stay as written.
    /// </summary>
    public sealed class ModIni
    {
        public string Path { get; }
        public List<IniSection> Sections { get; } = new List<IniSection>();

        private ModIni(string path) => Path = path;

        public static ModIni Load(string path)
        {
            var ini = new ModIni(path);
            IniSection current = null;
            var conditions = new Stack<string>();

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = Strip(raw);
                if (line.Length == 0)
                    continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    // A section header closes any block left open by a malformed file.
                    conditions.Clear();
                    current = new IniSection(line.Substring(1, line.Length - 2).Trim());
                    ini.Sections.Add(current);
                    continue;
                }

                var word = FirstWord(line);
                if (word.Equals("if", StringComparison.OrdinalIgnoreCase))
                {
                    conditions.Push(line.Substring(2).Trim());
                    continue;
                }
                if (word.Equals("else", StringComparison.OrdinalIgnoreCase))
                {
                    var previous = conditions.Count > 0 ? conditions.Pop() : "";
                    conditions.Push("not (" + previous + ")");
                    continue;
                }
                if (word.Equals("endif", StringComparison.OrdinalIgnoreCase))
                {
                    if (conditions.Count > 0)
                        conditions.Pop();
                    continue;
                }

                if (current == null)
                    continue;               // a stray line before the first section

                var split = line.IndexOf('=');
                var key = split < 0 ? line : line.Substring(0, split).Trim();
                var value = split < 0 ? "" : line.Substring(split + 1).Trim();
                current.Entries.Add(new IniEntry(key, value,
                    conditions.Count > 0 ? string.Join(" and ", conditions.ToArray()) : null));
            }
            return ini;
        }

        public IniSection Section(string name)
        {
            foreach (var s in Sections)
            {
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return null;
        }

        private static string Strip(string line)
        {
            var comment = line.IndexOf(';');
            if (comment >= 0)
                line = line.Substring(0, comment);
            return line.Trim();
        }

        private static string FirstWord(string line)
        {
            var end = 0;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '=')
                end++;
            return line.Substring(0, end);
        }
    }
}
