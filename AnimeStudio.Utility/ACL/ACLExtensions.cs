using System;
using ACLLibs;

namespace AnimeStudio
{
    public static class ACLExtensions
    {
        /// <summary>
        /// Single dispatch point from a parsed ACL clip to the native decoder that can read it.
        /// The clip type carries the layout, so no game checks are needed below the SR split.
        /// </summary>
        public static void Process(this ACLClip m_ACLClip, Game game, out float[] values, out float[] times)
        {
            if (game.Type.IsSRGroup())
            {
                var aclClip = m_ACLClip as MHYACLClip;
                SRACL.DecompressAll(aclClip.m_ClipData, out values, out times);
            }
            else
            {
                switch (m_ACLClip)
                {
                    case GIACLClip giaclClip:
                        DBACL.DecompressTracks(giaclClip.m_ClipData, giaclClip.m_DatabaseData, out values, out times);
                        break;
                    // Must precede MHYACLClip -- ZZZACLClip derives from it.
                    case ZZZACLClip zzzaclClip:
                        DBACL.DecompressTracksZZZ(zzzaclClip.m_TransformData, zzzaclClip.m_ScalarData, zzzaclClip.m_databaseData, zzzaclClip.m_DatabaseData, out values, out times);
                        break;
                    case MHYACLClip mhyaclClip:
                        ACL.DecompressAll(mhyaclClip.m_ClipData, out values, out times);
                        break;
                    default:
                        values = Array.Empty<float>();
                        times = Array.Empty<float>();
                        break;
                }
            }
        }
    }
}
