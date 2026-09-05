namespace Compression.Registry;

/// <summary>
/// Opt-in: the descriptor names the entries its own <c>List</c> renders from the
/// container itself — a whole-image view, a metadata rendering, a raw structure or
/// log dump — rather than from anything stored in it.
/// <para>
/// Those names are re-rendered for as long as the container exists, so asking the
/// modifier to drop one is meaningless and finding one after a purge proves nothing.
/// Declaring them lets <see cref="RebuildVerb.PurgeViaModifier"/> tell "nothing was
/// removed" from "everything removable was" without paying for a reference empty
/// container, and covers renderings an empty container does not produce because it
/// has no log to render.
/// </para>
/// </summary>
public interface ISyntheticEntryNames {
  /// <summary>
  /// The listed names that are renderings of the container. Must never include a
  /// name under which user content is stored.
  /// </summary>
  IReadOnlySet<string> SyntheticEntryNames { get; }
}
