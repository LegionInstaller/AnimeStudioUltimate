using System;

namespace AnimeStudio
{
    /// <summary>
    /// What a converter hands over when something else may supply a mesh in place of the one
    /// an asset carries -- a mesh out of a 3DMigoto mod folder, for instance.
    ///
    /// The type lives in the core so the converter and whoever answers the call need not know
    /// about each other: the converter offers the hook, an outside project fills it in.
    /// </summary>
    public sealed class MeshReplacementContext
    {
        /// <summary>The renderer whose mesh is about to be converted.</summary>
        public Renderer Renderer { get; set; }

        /// <summary>The mesh it would otherwise use. Its bind poses stay in force.</summary>
        public Mesh Original { get; set; }

        /// <summary>Frame path of a bone transform, in the converter's own naming.</summary>
        public Func<Transform, string> PathOf { get; set; }

        /// <summary>Material name of the original mesh's n-th submesh.</summary>
        public Func<int, string> MaterialOf { get; set; }
    }
}
