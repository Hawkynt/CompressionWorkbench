#pragma warning disable CS1591
namespace FileFormat.Ghost;

/// <summary>
/// Best-effort hint at which Ghost generation produced an image, based on
/// the file-header bytes. <see cref="Unknown"/> is the expected result for
/// arbitrary or truncated payloads — Symantec has never published the
/// format spec.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Modern11Plus"/> classification is the only one the R/W
/// path acts on: the file header's <c>FE EF</c> magic at offset 0 plus a
/// recognised compression byte at offset 3 + a valid Track-0 / partition
/// record discoverable within the first ~64 KB.
/// </para>
/// <para>
/// <see cref="PossiblyLegacy4To7"/> is recorded for diagnostic purposes
/// only — the legacy DOS-era Ghost 4-7 framing is different enough from
/// the Ghost 11.x-12.x record container that even the parse path
/// version-gates and refuses to attempt extraction, surfacing a clear
/// error instead of silent corruption.
/// </para>
/// </remarks>
public enum GhostGenerationHint {
  /// <summary>
  /// Specifies an unknown or unrecognized value.
  /// </summary>
Unknown = 0,
  /// <summary>
  /// Specifies the possibly legacy 4 to 7 option.
  /// </summary>
PossiblyLegacy4To7 = 1,
  /// <summary>
  /// Specifies the possibly modern 8 plus option.
  /// </summary>
PossiblyModern8Plus = 2,
  /// <summary>
  /// Ghost 11.x / 12.x record container — the format <see cref="GhostReader"/>
  /// is reverse-engineered against (via nyarime/gho). Parses fully when this
  /// hint is set.
  /// </summary>
  Modern11Plus = 3,
  /// <summary>
  /// Pre-3.0 (Ghost 1.x / 2.x DOS-era) dump file — FE EF magic + dump-head
  /// type byte at offset 2 + 512-byte zero-padded head, no record framing
  /// magic and no compression. Parsed by <see cref="GhostLegacyReader"/>
  /// to Stage-1 R/O metadata.
  /// </summary>
  PreModern1And2 = 4,
}
