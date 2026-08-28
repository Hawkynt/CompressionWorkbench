namespace Compression.Registry;

/// <summary>
/// Flags describing what operations a format supports.
/// <para>
/// Write capability is a four-level scale:
/// </para>
/// <list type="bullet">
///   <item><description><b>Unsupported</b> — no descriptor exists.</description></item>
///   <item><description><b>Read-Only</b> — <see cref="CanList"/> and/or <see cref="CanExtract"/> only.</description></item>
///   <item><description><b>WORM</b> (Write-Once-Read-Many) — adds <see cref="CanCreate"/>: a fresh archive/image can be produced, but the library has no supported edit of an existing instance.</description></item>
///   <item><description><b>R/W</b> (Modify) — adds <see cref="CanModify"/>: an existing instance supports add/replace/remove and remains valid after the edit.</description></item>
/// </list>
/// <para>
/// <b>R/W describes the public operation, not the physical write strategy.</b>
/// A format may update allocation metadata in place, append a new index, relayout members,
/// or rebuild the complete image. Those are implementation choices. If callers can open an
/// existing instance, apply add/replace/remove through <see cref="IArchiveModifiable"/>, and
/// obtain a valid instance preserving the semantics the implementation claims to support,
/// the format is R/W at this API surface. Conversely, merely having a writer for fresh images
/// is WORM and must not set <see cref="CanModify"/>.
/// </para>
/// <para>
/// This distinction is especially important for read-only-on-mount filesystem formats such as
/// SquashFS, CramFS and EROFS: the native filesystem driver may intentionally forbid mounted
/// writes while an offline image editor can still support complete, deterministic mutation by
/// relayout/rebuild. <see cref="CanModify"/> reports the latter capability.
/// </para>
/// </summary>
[Flags]
public enum FormatCapabilities {
  None = 0,
  CanList = 1 << 0,
  CanExtract = 1 << 1,
  /// <summary>WORM: can produce a fresh archive/image, but has no supported existing-instance edit.</summary>
  CanCreate = 1 << 2,
  CanTest = 1 << 3,
  SupportsPassword = 1 << 4,
  SupportsMultipleEntries = 1 << 5,
  SupportsDirectories = 1 << 6,
  SupportsOptimize = 1 << 8,
  CanCompoundWithTar = 1 << 9,
  /// <summary>R/W: can add/replace/remove entries in an existing archive/image. The implementation may edit in place or relayout/rebuild. Implies <see cref="CanCreate"/> for normal writable formats.</summary>
  CanModify = 1 << 10,
}