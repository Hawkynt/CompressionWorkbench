#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can enumerate the real byte-level layout
/// of an archive — every entry's header, compressed payload, and inter-entry
/// gaps at their actual offsets. Parallel to <see cref="IFilesystemExtentMap"/>
/// but for archive formats (ZIP, 7z, TAR, LZH, ARJ, etc.).
///
/// <para><b>Fail-closed contract:</b> omitted bytes are interpreted as unused by
/// maintenance consumers. Any live, structural, ambiguous or undecoded region
/// must therefore be emitted as <see cref="DefragBlockKind.MetadataReserved"/>
/// (or <see cref="DefragBlockKind.Used"/>), never silently omitted. If a layout
/// cannot be proven safe, return no extents and the inherited generic wipe is a
/// no-op.</para>
///
/// <para>That exact preservation map also makes every implementation an
/// <see cref="IWipeEmpty"/> capability. The generic implementation zeros proven
/// dead gaps while format-specific overrides may additionally scrub tombstones,
/// reserved growth records, stale indexes, or other recoverable metadata.</para>
///
/// <para>Drives the Defragment/Optimize window block-map preview so the user
/// sees the real archive layout before pressing "Optimize".</para>
/// </summary>
public interface IArchiveLayoutMap : IWipeEmpty {
  /// <summary>
  /// Enumerates the actual byte layout of <paramref name="archive"/>.
  /// Coverage may be sparse only where omitted bytes are proven unused; callers
  /// fill those gaps with <see cref="DefragBlockKind.Free"/>. The stream's
  /// position may be modified during enumeration but the caller owns the
  /// lifetime — implementations must not dispose <paramref name="archive"/>.
  /// </summary>
  IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive);
}
