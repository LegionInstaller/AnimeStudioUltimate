using System;
using System.Threading;

namespace AnimeStudio
{
    public static class Progress
    {
        public static bool Silent = false;
        public static IProgress<int> Default = new Progress<int>();
        private static int preValue;

        public static void Reset()
        {
            if (!Silent)
            {
                Interlocked.Exchange(ref preValue, 0);
                Default.Report(0);
            }
        }

        public static void Report(int current, int total)
        {
            if (!Silent && total > 0)
            {
                var value = (int)(current * 100f / total);
                Report(value);
            }
        }

        // Loading reports from several threads at once. Only publish a value that is actually
        // higher than the last one published, decided atomically, so the bar cannot jump
        // backwards when two threads report out of order.
        private static void Report(int value)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref preValue);
                if (value <= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref preValue, value, previous) != previous);

            Default.Report(value);
        }
    }
}
