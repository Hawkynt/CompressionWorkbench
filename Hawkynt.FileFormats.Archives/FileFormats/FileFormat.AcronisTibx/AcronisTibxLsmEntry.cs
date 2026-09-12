#pragma warning disable CS1591
namespace FileFormat.AcronisTibx;

/// <summary>
///   One logical entry surfaced from a Stage-2 LSM tree walk over a <c>.tibx</c> container.
///   This pass classifies pages by type (HDR / LSM_LEAF / LSM_DIR / GOLOMB / DATA / CI) and
///   surfaces a per-page summary; it does NOT yet decode the LSM record stream inside LEAF
///   pages (the Golomb-coded key/value layout in <c>lsm_lookup.c</c> + <c>golomb.c</c> is the
///   next stage and would expose the actual filenames).
/// </summary>
/// <remarks>
///   <para>
///     The <see cref="LsmSubHeader"/> property is set for LSM_LEAF and LSM_DIR pages — it
///     carries the <c>(version, encoding, count, len, zlen, seq, id)</c> tuple recovered from
///     the page sub-header at <c>+0xC..+0x1C</c>. <c>Count</c> is the number of LSM key/value
///     records on that LEAF page, <c>Len</c> / <c>Zlen</c> are the uncompressed and on-disk
///     compressed body lengths, and <c>Seq</c> + <c>Id</c> identify which ctree this page
///     belongs to.
///   </para>
/// </remarks>
public sealed class AcronisTibxLsmEntry {

  /// <summary>1-based page index counted from the start of the container.</summary>
  public required long PageIndex { get; init; }

  /// <summary>Byte offset of the page within the container.</summary>
  public required long FileOffset { get; init; }

  /// <summary>Page-type tag at <c>+0x1</c> of the page frame.</summary>
  public required AcronisTibxPageType PageType { get; init; }

  /// <summary>4-byte ASCII content magic — see <see cref="AcronisTibxPage.ContentMagic"/>.</summary>
  public required byte[] ContentMagic { get; init; }

  /// <summary>Stored BE32 CRC at <c>+0x4</c>. Zero for the page-zero HDR page.</summary>
  public required uint StoredCrc { get; init; }

  /// <summary>
  ///   LSM sub-header (only set for LSM_LEAF / LSM_DIR pages — <c>null</c> for HDR /
  ///   GOLOMB / DATA / CI).
  /// </summary>
  public AcronisTibxLsmPageSubHeader? LsmSubHeader { get; init; }
}
