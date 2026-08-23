using System.Collections.Generic;

namespace AnimeStudio
{
    /// <summary>
    /// The two members the loader needs from every container format it unpacks.
    /// </summary>
    /// <remarks>
    /// The loader used to reach these through <c>dynamic</c>. That pulls the C# runtime binder
    /// into the load path, and finalizing its generated dynamic methods accounted for 8% of a
    /// measured export run. The container classes keep their public fields; each one just
    /// exposes them explicitly here, so nothing else has to change.
    /// </remarks>
    public interface IBundleContainer
    {
        BundleFile.Header Header { get; }
        List<StreamFile> Files { get; }
    }
}
