namespace Compression.Registry;

/// <summary>
/// Opt-in capability for rebuilding an existing container while preserving already encoded payloads.
/// </summary>
/// <remarks>
/// Remuxing is independent of fresh archive/container creation and archive-style mutation. A remuxer
/// requires an existing source container and may rewrite framing, timing tables, indexes or interleaving
/// while leaving encoded packet payloads untouched unless explicit replacements are supplied.
/// </remarks>
public interface IContainerRemuxable {
  /// <summary>
  /// Rebuilds <paramref name="source"/> into <paramref name="output"/>, applying the supplied
  /// addressable pseudo-entry replacements without decoding or re-encoding preserved payloads.
  /// Inputs omitted from <paramref name="replacements"/> remain sourced from the existing container.
  /// </summary>
  /// <param name="source">Existing valid source container.</param>
  /// <param name="output">Destination for the rebuilt container.</param>
  /// <param name="replacements">Format-specific addressable payload replacements.</param>
  /// <param name="options">Format-specific remux options.</param>
  void Remux(
    Stream source,
    Stream output,
    IReadOnlyList<ArchiveInputInfo> replacements,
    FormatCreateOptions options
  );
}