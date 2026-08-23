using System;
using System.Runtime.CompilerServices;

namespace AnimeStudio
{
    /// <summary>
    /// Interpolated string handler for <see cref="Logger.Verbose(ref VerboseLogHandler)"/>.
    /// <para>
    /// Verbose logging is off during normal runs, but an interpolated argument is evaluated at
    /// the call site regardless of what the callee does with it. With 234 <c>Logger.Verbose($"...")</c>
    /// sites -- several of them inside per-object and per-block loops -- that meant building and
    /// discarding a string for every single asset that was parsed.
    /// </para>
    /// <para>
    /// The <c>out bool</c> constructor parameter makes the compiler guard the whole interpolation:
    /// when verbose logging is disabled nothing is formatted, nothing is boxed and nothing is
    /// allocated, so the call costs one flag test. Call sites stay exactly as they are.
    /// </para>
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct VerboseLogHandler
    {
        private DefaultInterpolatedStringHandler inner;

        public VerboseLogHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            if (Logger.VerboseEnabled)
            {
                inner = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
                shouldAppend = true;
            }
            else
            {
                inner = default;
                shouldAppend = false;
            }
        }

        public void AppendLiteral(string value) => inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string format) => inner.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment) => inner.AppendFormatted(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(string value) => inner.AppendFormatted(value);
        public void AppendFormatted(ReadOnlySpan<char> value) => inner.AppendFormatted(value);

        internal string ToStringAndClear() => inner.ToStringAndClear();
    }
}
