namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can rebuild an archive into a new output stream,
/// stepping down to the smallest size in <see cref="CanonicalSizes"/> that still holds
/// the current payload. For formats with continuous sizing (most filesystems),
/// <see cref="CanonicalSizes"/> returns just the current image size and shrink tight-packs
/// to exactly that. For fixed-size disk image families (C64 D64/D71/D81; PC 720K/1.44M/2.88M
/// floppies; Amiga ADF), the list walks from largest to smallest standard size.
/// </summary>
public interface IArchiveShrinkable {
  /// <summary>
  /// Canonical image sizes in bytes, ascending. A 1.44 MB PC floppy descriptor returns
  /// <c>[737280, 1474560, 2949120]</c>; a filesystem without a disc-size concept returns
  /// a single-element list carrying the current payload-tight size.
  /// </summary>
  IReadOnlyList<long> CanonicalSizes { get; }

  /// <summary>
  /// Rebuilds <paramref name="input"/> into <paramref name="output"/>, picking the
  /// smallest <see cref="CanonicalSizes"/> entry that holds the current content.
  /// </summary>
  void Shrink(Stream input, Stream output);
}
