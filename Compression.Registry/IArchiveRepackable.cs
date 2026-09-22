namespace Compression.Registry;

/// <summary>
/// Opt-in capability for rebuilding a container from the same logical entries.
/// Repacking may change physical placement, headers and packing boundaries, but
/// is not by itself compression optimization.
/// </summary>
public interface IArchiveRepackable {
  /// <summary>
  /// Rebuilds <paramref name="input"/> into <paramref name="output"/> while
  /// preserving the logical entry set and contents.
  /// </summary>
  void Repack(Stream input, Stream output);
}
