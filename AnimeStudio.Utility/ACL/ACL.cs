using System;
using System.Runtime.InteropServices;
using AnimeStudio.PInvoke;

namespace ACLLibs
{
    public struct DecompressedClip
    {
        public IntPtr Values;
        public int ValuesCount;
        public IntPtr Times;
        public int TimesCount;
    }

    /// <summary>
    /// Copies a managed buffer to a 16-byte-aligned native address. Every acl decoder reinterprets
    /// the buffer in place as a compressed_tracks/compressed_database header, which requires that
    /// alignment. A null or empty buffer yields <see cref="IntPtr.Zero"/>, which the decoders treat
    /// as "not supplied" -- passing a pointer to an empty allocation instead would have them parse
    /// uninitialized heap.
    /// </summary>
    internal readonly struct AlignedBuffer : IDisposable
    {
        private readonly IntPtr _allocation;
        public readonly IntPtr Pointer;

        public AlignedBuffer(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                _allocation = IntPtr.Zero;
                Pointer = IntPtr.Zero;
                return;
            }

            _allocation = Marshal.AllocHGlobal(data.Length + 16);
            Pointer = new IntPtr(16 * (((long)_allocation + 15) / 16));
            Marshal.Copy(data, 0, Pointer, data.Length);
        }

        public void Dispose()
        {
            if (_allocation != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_allocation);
            }
        }
    }

    internal static class DecompressedClipExtensions
    {
        /// <summary>
        /// Copies the native result into managed arrays. A decoder that rejected its input leaves
        /// the clip zeroed, so the counts are checked before dereferencing the pointers.
        /// </summary>
        public static void CopyOut(this in DecompressedClip clip, out float[] values, out float[] times)
        {
            if (clip.Values != IntPtr.Zero && clip.ValuesCount > 0)
            {
                values = new float[clip.ValuesCount];
                Marshal.Copy(clip.Values, values, 0, clip.ValuesCount);
            }
            else
            {
                values = Array.Empty<float>();
            }

            if (clip.Times != IntPtr.Zero && clip.TimesCount > 0)
            {
                times = new float[clip.TimesCount];
                Marshal.Copy(clip.Times, times, 0, clip.TimesCount);
            }
            else
            {
                times = Array.Empty<float>();
            }
        }
    }

    public static class ACL
    {
        private const string DLL_NAME = "acl";
        static ACL()
        {
            DllLoader.PreloadDll(DLL_NAME);
        }
        public static void DecompressAll(byte[] data, out float[] values, out float[] times)
        {
            var decompressedClip = new DecompressedClip();
            DecompressAll(data, ref decompressedClip);

            decompressedClip.CopyOut(out values, out times);

            Dispose(ref decompressedClip);
        }

        #region importfunctions

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressAll(byte[] data, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref DecompressedClip decompressedClip);

        #endregion
    }

    public static class SRACL
    {
        private const string DLL_NAME = "AnimeStudio.ACL.SR";
        static SRACL()
        {
            // x64 only, so it lives in the application directory rather than in x86/x64.
            DllLoader.PreloadDll(DLL_NAME, archSpecific: false);
        }
        public static void DecompressAll(byte[] data, out float[] values, out float[] times)
        {
            var decompressedClip = new DecompressedClip();
            DecompressClip(data, ref decompressedClip);

            decompressedClip.CopyOut(out values, out times);

            Dispose(ref decompressedClip);
        }

        #region importfunctions

        // This one is the acl 1.x uniformly-sampled decoder; its export is named DecompressClip.
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressClip(byte[] data, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref DecompressedClip decompressedClip);

        #endregion
    }

    /// <summary>
    /// The database-backed decoders. Two native backends share this wrapper:
    /// <list type="bullet">
    /// <item>AnimeStudio.ACL.DB -- acl 2.1.99 (mhy), used for Genshin. Takes one buffer holding the
    /// transform and scalar tracks back to back.</item>
    /// <item>AnimeStudio.ACL.ZZZ -- acl 2.1.0 (vHoYo), the only tree that decodes ZZZ. Takes the
    /// transform and scalar tracks as separate buffers because ZZZ stores them that way.</item>
    /// </list>
    /// Both derive the database bulk data themselves when it is not passed separately, so neither
    /// needs the caller to know how a given game lays its database out.
    /// </summary>
    public static class DBACL
    {
        private const string DLL_NAME = "AnimeStudio.ACL.DB";
        private const string DLL_NAME_ZZZ = "AnimeStudio.ACL.ZZZ";
        static DBACL()
        {
            // x64 only, so they live in the application directory rather than in x86/x64.
            DllLoader.PreloadDll(DLL_NAME, archSpecific: false);
            DllLoader.PreloadDll(DLL_NAME_ZZZ, archSpecific: false);
        }

        /// <summary>
        /// ZZZ: transform and scalar tracks arrive as separate blobs, and the database bulk data
        /// arrives from the clip's streamed resource rather than inline.
        /// </summary>
        public static void DecompressTracksZZZ(byte[] transformData, byte[] scalarData, byte[] databaseData, byte[] bulkData, out float[] values, out float[] times)
        {
            var decompressedClip = new DecompressedClip();

            using (var transform = new AlignedBuffer(transformData))
            using (var scalar = new AlignedBuffer(scalarData))
            using (var database = new AlignedBuffer(databaseData))
            using (var bulk = new AlignedBuffer(bulkData))
            {
                // Sizes travel with the pointers: acl trusts the size fields inside the blobs, and
                // a clip whose header over-claims would otherwise read past the buffer and kill the
                // process with an access violation that no managed handler can catch.
                DecompressTracksZZZ(
                    transform.Pointer, transformData?.Length ?? 0,
                    scalar.Pointer, scalarData?.Length ?? 0,
                    database.Pointer, databaseData?.Length ?? 0,
                    bulk.Pointer, bulkData?.Length ?? 0,
                    ref decompressedClip);
            }

            decompressedClip.CopyOut(out values, out times);

            DisposeZZZ(ref decompressedClip);
        }

        /// <summary>
        /// Genshin: one clip buffer, database bulk data appended behind the database blob.
        /// </summary>
        public static void DecompressTracks(byte[] data, byte[] db, out float[] values, out float[] times)
        {
            var decompressedClip = new DecompressedClip();

            using (var clip = new AlignedBuffer(data))
            using (var database = new AlignedBuffer(db))
            {
                // Null streamer: the native side derives the bulk pointer from the database blob.
                DecompressTracks(clip.Pointer, database.Pointer, IntPtr.Zero, ref decompressedClip);
            }

            decompressedClip.CopyOut(out values, out times);

            Dispose(ref decompressedClip);
        }

        #region importfunctions

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressTracks(nint data, nint db, nint streamer, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME_ZZZ, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DecompressTracksZZZ")]
        private static extern void DecompressTracksZZZ(nint transformTracks, int transformSize, nint scalarTracks, int scalarSize, nint database, int databaseSize, nint bulkData, int bulkSize, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME_ZZZ, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Dispose")]
        private static extern void DisposeZZZ(ref DecompressedClip decompressedClip);

        #endregion
    }
}
