using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.Migoto
{
    /// <summary>
    /// One term of an <c>if</c>: a variable compared against a value. An <c>else</c> branch
    /// carries the negated terms of the branches before it.
    ///
    /// Anything the reader cannot make sense of -- <c>DRAW_TYPE == 2 || DRAW_TYPE == 4</c>
    /// and the like -- becomes an opaque term that always holds. Dropping geometry over a
    /// misread condition is the worse mistake; the part is flagged instead.
    ///
    /// A test joined by <c>||</c> is one term as well, and named by its text like an opaque
    /// one -- no single variable stands for it. But when each side can be read it is judged:
    /// <c>$th == 0 || $th == 1</c> picks one of two texture sets, and both always holding
    /// would hand out the wrong one.
    /// </summary>
    public sealed class IniCondition
    {
        public string Variable { get; set; }
        public string Value { get; set; }
        public bool Negate { get; set; }
        public bool Opaque { get; set; }
        public string Text { get; set; }

        /// <summary>The sides of a readable <c>||</c>, each a list of terms that apply
        /// together. Null for everything else.</summary>
        public List<List<IniCondition>> AnyOf { get; set; }

        /// <summary>The plain comparisons in here: this one, or those of a readable
        /// <c>||</c>.</summary>
        public IEnumerable<IniCondition> Terms =>
            AnyOf == null ? new[] { this } : AnyOf.SelectMany(side => side.SelectMany(c => c.Terms));

        /// <summary>Whether the term can be judged: a plain comparison or a readable
        /// <c>||</c>.</summary>
        public bool Readable => !Opaque || AnyOf != null;

        public bool Holds(IReadOnlyDictionary<string, string> chosen)
        {
            if (AnyOf != null)
                return AnyOf.Any(side => side.All(c => c.Holds(chosen))) != Negate;
            if (Opaque || Variable == null)
                return true;
            if (!chosen.TryGetValue(Variable, out var value))
                value = "0";
            var same = string.Equals(value, Value, StringComparison.OrdinalIgnoreCase);
            return Negate ? !same : same;
        }

        public override string ToString() =>
            Opaque ? Text : $"{Variable} {(Negate ? "!=" : "==")} {Value}";
    }

    /// <summary>
    /// One entry of an ini section. Keys repeat -- a section can hold many
    /// <c>drawindexed</c> lines -- so entries stay an ordered list, and each remembers the
    /// conditions of every <c>if</c> it sat inside, which apply together.
    /// </summary>
    public sealed class IniEntry
    {
        public string Key { get; }
        public string Value { get; }
        public List<IniCondition> Conditions { get; }

        public IniEntry(string key, string value, List<IniCondition> conditions)
        {
            Key = key;
            Value = value;
            Conditions = conditions ?? new List<IniCondition>();
        }

        public bool IsConditional => Conditions.Count > 0;

        public string ConditionText =>
            Conditions.Count == 0 ? null : string.Join(" and ", Conditions.Select(c => c.ToString()));

        public override string ToString() =>
            Conditions.Count == 0 ? $"{Key} = {Value}" : $"[{ConditionText}] {Key} = {Value}";
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
    /// A 3DMigoto ini, read literally -- sections, ordered entries, the conditions around
    /// them, and the variables a mod switches its variants with.
    ///
    /// The dialect is small: <c>[Section]</c> headers, <c>key = value</c>, <c>;</c> comments,
    /// and <c>if</c> / <c>else if</c> / <c>else</c> / <c>endif</c> around draw calls, which
    /// nest. Nothing is interpreted here beyond that; <see cref="MigotoMod"/> does the rest.
    /// </summary>
    public sealed class ModIni
    {
        public string Path { get; }
        public List<IniSection> Sections { get; } = new List<IniSection>();

        /// <summary>
        /// The variables the mod switches with, and the values each can take. A
        /// <c>[KeySwap]</c> section spells them out (<c>$body = 0,1,2</c>); anything else
        /// seen in a condition is assumed to be a plain on/off.
        /// </summary>
        public Dictionary<string, List<string>> Variables { get; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private ModIni(string path) => Path = path;

        /// <summary>One open <c>if</c>: what the current branch demands, and what the
        /// branches before it ruled out.</summary>
        private sealed class Block
        {
            public List<IniCondition> Ruled = new List<IniCondition>();
            public List<IniCondition> Current = new List<IniCondition>();
        }

        public static ModIni Load(string path)
        {
            var ini = new ModIni(path);
            IniSection current = null;
            var blocks = new List<Block>();

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = Strip(raw);
                if (line.Length == 0)
                    continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    // A section header closes anything a malformed file left open.
                    blocks.Clear();
                    current = new IniSection(line.Substring(1, line.Length - 2).Trim());
                    ini.Sections.Add(current);
                    continue;
                }

                var word = FirstWord(line).ToLowerInvariant();
                if (word == "if")
                {
                    blocks.Add(new Block { Current = Parse(line.Substring(2)) });
                    continue;
                }
                if (word == "else")
                {
                    if (blocks.Count == 0)
                        continue;
                    var block = blocks[blocks.Count - 1];
                    // What this branch demanded is what the ones after it must rule out.
                    // Several terms are ruled out as one: "not (a and b)" still lets a
                    // through without b, which negating each on its own would not.
                    if (block.Current.Count > 1 && block.Current.All(c => c.Readable))
                        block.Ruled.Add(new IniCondition
                        {
                            Opaque = true,
                            Text = "!(" + string.Join(" && ", block.Current) + ")",
                            AnyOf = new List<List<IniCondition>> { block.Current },
                            Negate = true,
                        });
                    else
                        foreach (var c in block.Current)
                            if (!c.Opaque)
                                block.Ruled.Add(new IniCondition
                                { Variable = c.Variable, Value = c.Value, Negate = !c.Negate });
                            else if (c.AnyOf != null)
                                block.Ruled.Add(new IniCondition
                                { Opaque = true, Text = "!(" + c.Text + ")", AnyOf = c.AnyOf, Negate = true });
                    var rest = line.Substring(4).TrimStart();
                    block.Current = rest.StartsWith("if", StringComparison.OrdinalIgnoreCase)
                        ? Parse(rest.Substring(2))
                        : new List<IniCondition>();
                    continue;
                }
                if (word == "endif")
                {
                    if (blocks.Count > 0)
                        blocks.RemoveAt(blocks.Count - 1);
                    continue;
                }

                if (current == null)
                    continue;               // a stray line before the first section

                var split = line.IndexOf('=');
                var key = split < 0 ? line : line.Substring(0, split).Trim();
                var value = split < 0 ? "" : line.Substring(split + 1).Trim();

                var conditions = new List<IniCondition>();
                foreach (var block in blocks)
                {
                    conditions.AddRange(block.Ruled);
                    conditions.AddRange(block.Current);
                }
                current.Entries.Add(new IniEntry(key, value, conditions));
                ini.Note(key, value);
            }
            ini.NoteFromConditions();
            return ini;
        }

        /// <summary>
        /// Picks up a variable declaration. A key swap writes <c>$body = 0,1,2</c>, a
        /// constant <c>global persist $body = 0</c>; both name the variable, only the first
        /// says what it can be.
        /// </summary>
        private void Note(string key, string value)
        {
            var name = key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault(w => w.StartsWith("$"));
            if (name == null)
                return;
            var choices = value.Split(',')
                               .Select(v => v.Trim())
                               .Where(v => v.Length > 0)
                               .Distinct()
                               .ToList();
            if (choices.Count <= 1)
            {
                if (!Variables.ContainsKey(name))
                    Variables[name] = new List<string>();
                return;
            }
            Variables[name] = choices;
        }

        /// <summary>Variables only ever seen in a condition still need their values known.</summary>
        private void NoteFromConditions()
        {
            foreach (var section in Sections)
                foreach (var entry in section.Entries)
                    foreach (var condition in entry.Conditions.SelectMany(c => c.Terms))
                    {
                        if (condition.Variable == null)
                            continue;
                        if (!Variables.TryGetValue(condition.Variable, out var values))
                            Variables[condition.Variable] = values = new List<string>();
                        if (values.Count > 0 && !values.Contains(condition.Value))
                            values.Add(condition.Value);
                        else if (values.Count == 0)
                            values.Add(condition.Value);
                    }
            foreach (var key in Variables.Keys.ToList())
            {
                var values = Variables[key];
                if (values.Count == 0)
                    Variables[key] = new List<string> { "0", "1" };
                else if (!values.Contains("0"))
                    values.Insert(0, "0");
            }
        }

        /// <summary>
        /// Reads the expression of an <c>if</c>. Only <c>$name == value</c>, joined by
        /// <c>&amp;&amp;</c>, is understood; anything else is kept as an opaque term that
        /// always holds, so a condition nobody can read never silently drops geometry.
        ///
        /// Such chains joined by <c>||</c> are understood too, as one term. Brackets are
        /// followed, so a term that cannot be read costs only itself: in
        /// <c>$chain == 1 &amp;&amp; ($body &gt;= 3 || ...)</c> the first half still counts.
        /// </summary>
        private static List<IniCondition> Parse(string expression)
        {
            var result = new List<IniCondition>();
            var text = Unwrap(expression);
            if (text.Length == 0)
                return result;

            var sides = SplitOutside(text, "||");
            if (sides.Count > 1)
            {
                var read = sides.Select(Parse).ToList();
                if (read.Any(s => s.Count == 0 || s.Any(c => c.Opaque)))
                    read = null;
                result.Add(new IniCondition { Opaque = true, Text = text, AnyOf = read });
                return result;
            }

            var terms = SplitOutside(text, "&&");
            if (terms.Count > 1)
            {
                foreach (var term in terms)
                    result.AddRange(Parse(term));
                return result;
            }

            var at = text.IndexOf("==", StringComparison.Ordinal);
            var negate = false;
            if (at < 0)
            {
                at = text.IndexOf("!=", StringComparison.Ordinal);
                negate = at >= 0;
            }
            var name = at >= 0 ? text.Substring(0, at).Trim() : null;
            if (at < 0 || !name.StartsWith("$") || text.IndexOfAny(new[] { '(', ')', '|', '&' }) >= 0)
                result.Add(new IniCondition { Opaque = true, Text = text });
            else
                result.Add(new IniCondition
                {
                    Variable = name,
                    Value = text.Substring(at + 2).Trim(),
                    Negate = negate,
                });
            return result;
        }

        /// <summary>Splits at an operator, but not inside brackets.</summary>
        private static List<string> SplitOutside(string text, string op)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')')
                    depth--;
                else if (depth == 0 && string.CompareOrdinal(text, i, op, 0, op.Length) == 0)
                {
                    parts.Add(text.Substring(start, i - start));
                    start = i + op.Length;
                    i = start - 1;
                }
            }
            parts.Add(text.Substring(start));
            return parts;
        }

        /// <summary>Takes off the brackets around a whole expression.</summary>
        private static string Unwrap(string text)
        {
            text = text.Trim();
            while (text.Length > 1 && text[0] == '(' && text[text.Length - 1] == ')')
            {
                // Only when the first bracket closes at the very end: "(a) && (b)" stays.
                int depth = 0, close = -1;
                for (int i = 0; i < text.Length && close < 0; i++)
                {
                    if (text[i] == '(')
                        depth++;
                    else if (text[i] == ')' && --depth == 0)
                        close = i;
                }
                if (close != text.Length - 1)
                    break;
                text = text.Substring(1, text.Length - 2).Trim();
            }
            return text;
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
