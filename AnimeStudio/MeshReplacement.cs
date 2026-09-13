using System;
using System.Collections.Generic;

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

        /// <summary>
        /// The material the submesh at hand would otherwise use. A renderer often carries
        /// several -- a face has one for the face and one for the eyebrows -- and a mod that
        /// only repaints replaces exactly one of them.
        /// </summary>
        public string MaterialName { get; set; }

        /// <summary>
        /// The converter's material list. A replacement that brings its own textures adds a
        /// material here and names it on its submeshes instead of asking
        /// <see cref="MaterialOf"/>.
        /// </summary>
        public List<ImportedMaterial> Materials { get; set; }

        /// <summary>The converter's texture list. Anything a new material names goes here,
        /// because that is where the writer looks the name up.</summary>
        public List<ImportedTexture> Textures { get; set; }
    }
}
