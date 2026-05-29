#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can enumerate the real byte-level layout
/// of an archive — every entry's header, compressed payload, and inter-entry
/// gaps at their actual offsets. Parallel to <see cref="IFilesystemExtentMap"/>
/// but for archive formats (ZIP, 7z, TAR, LZH, ARJ, etc.).
///
/// <para>Drives the Defragment/Optimize window block-map preview so the user
/// sees the real archive layout before pressing "Optimize".</para>
/// </summary>
public interface IArchiveLayoutMap {
  /// <summary>
  /// Enumerates the actual byte layout of <paramref name="archive"/>.
  /// Coverage may be sparse; callers fill the gaps with
  /// <see cref="DefragBlockKind.Free"/>. The stream's position may be
  /// modified during enumeration but the caller owns the lifetime —
  /// implementations must not dispose <paramref name="archive"/>.
  /// </summary>
  IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive);
}
