#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ghost;

/// <summary>
/// Symantec / Norton Ghost backup-image descriptor — R/W for the
/// FE EF record container shared across the entire Binary Research →
/// Symantec → Norton lineage (v4 DOS-era through Ghost 11.x / 12.x).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The on-disk record container is reverse-engineered from
/// Norton Ghost 11.5.1 binaries (ported from the MIT-licensed
/// <c>nyarime/gho</c> Go implementation). Independent deep-RE confirms
/// the same struct shape (FE EF magic + 0x012F18D8 record framing +
/// 512-byte file/partition headers + 32 KiB blocks with 2-byte length
/// prefix + 0x01 raw-marker escape + Fast LZ Z1 codec) is shared across
/// Ghost 2003.775 / 2003.789 / Ghost 8 / Ghost 11.x — confirmed by
/// matching Forensic Focus's published byte patterns against the
/// nyarime-reversed struct. Round trip is verified by self-write-then-read
/// for stored, Fast LZ (Z1), and zlib levels 3-9 — with and without
/// password-based encryption.
/// </para>
/// <para>
/// <b>Legacy generations (v4-7 DOS-era).</b> The reader handles legacy
/// FE EF images via the same record walker — Binary Research's image
/// engine kept the container shape stable across the lineage. Writing
/// is gated on a real Symantec corpus for codec-byte validation; users
/// needing to write should use Symantec Ghost Explorer 2003.789
/// (free download from archive.org).
/// </para>
/// <para>
/// <b>Detection.</b> Magic <c>FE EF</c> at offset 0 with confidence 0.65
/// — the same magic is shared by other formats (e.g. Crusader 4-byte
/// headers) so we keep the confidence modest and rely on the registry's
/// extension hint (<c>.gho</c> / <c>.ghs</c>) to disambiguate.
/// </para>
/// </remarks>
public sealed class GhostFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "Ghost";
  public string DisplayName => "Symantec / Norton Ghost";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gho";
  public IReadOnlyList<string> Extensions => [".gho", ".ghs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xFE, 0xEF], Offset: 0, Confidence: 0.65)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("fastlz", "Fast LZ (Z1)"),
    new("zlib-3", "High zlib (Z3)"),
    new("zlib-9", "High zlib (Z9)")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  public string Description =>
    "Symantec / Norton Ghost — R/W (Create + Add/Remove/Replace) for the FE EF " +
    "record container shared across the entire Binary Research → Symantec → " +
    "Norton lineage (Ghost 3.0 / 1998 through Ghost 11.x / 12.x). " +
    "0x012F18D8 record framing, Fast LZ Z1 + zlib " +
    "Z3-Z9 compression, CRC-16 stream cipher encryption, .ghs spanning. " +
    "Modify is TRUE in-place append via the record stream: Add overwrites the " +
    "trailing end-of-image record (type 0x0023) with new partition / track-0 " +
    "records and re-emits the end record at the new EOF, leaving every byte " +
    "BEFORE the original end-record offset byte-identical. Replace + Remove " +
    "append annotation tombstones (CWB GHO1-magic record type 0x00FE) that the " +
    "reader honours via latest-write-wins. Each partition opens a fresh CRC-16 " +
    "cipher seeded from the password (per spec) so appending new partitions " +
    "needs no end-of-stream cipher snapshot. Forensic-delete callers go through " +
    "the legacy rebuild-based GhostModifier instead. Format " +
    "reverse-engineered from Norton Ghost 11.5.1 binaries (ported from " +
    "MIT-licensed nyarime/gho) and independently cross-confirmed against " +
    "Symantec Ghost Explorer 2003.789 — the Fast LZ \"123456789012345678\" hash " +
    "sentinel, the 0x9E5F hash multiplier, the 4096-entry hash table init, the " +
    "0x01 first-byte raw escape, the 16-bit LSB control word, the 2-byte " +
    "(b0,b1) match token format, the 0x012F18D8 record magic, the 10-byte " +
    "record-header layout, the CRC-16-XMODEM/CCITT stream cipher and the " +
    "None/Old/Fast/High compression dispatch are byte-identical between Ghost " +
    "2003.789 and Ghost 11.5.1. Writing is codec-validated against own reader; " +
    "byte-compat with Ghost Explorer has not yet been verified by an end-to-end " +
    "Wine round trip. Self-round-trip is test-covered for all supported " +
    "compression modes including encryption. " +
    "PRE-3.0 (Ghost 1.x / 2.x DOS-era, 1996-1998) images are R/O Stage-1 — the " +
    "pre-3.0 layout (FE EF magic + 512-byte dump head with a head-type byte at " +
    "offset 2: 0x01 disk descriptor, 0x02 partition, 0x03 boot record + " +
    "uncompressed body) was reverse-engineered from the Ghost 1.6 GHOST.EXE " +
    "binary (archive.org item ghost16, MD5 64cef43d0eb8d456de990cc95353fa05) by " +
    "binary inspection of the WriteDumpHeader (file_off 0x897d) and " +
    "ReadDumpHeader2 (file_off 0x8a6f) functions; the head magic + head-type " +
    "byte are surfaced via metadata.ini and the 512-byte dump head + body are " +
    "surfaced verbatim. Per-file extraction (FAT directory walk inside the " +
    "stored partition body) is out of scope for Stage-1 — pre-3.0 PKWARE-DCL " +
    "\"Old\" compression at byte 3 of the head was confirmed irrecoverable by " +
    "the cross-vendor binary RE; Ghost Explorer itself rejects it.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new GhostReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new GhostReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new GhostReader(archive, password: password);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"Ghost entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  // ── IArchiveCreatable ──────────────────────────────────────────────

  /// <summary>
  /// Produces a fresh Ghost 11.x / 12.x record container.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Inputs are mapped to Ghost records by leaf-name convention:
  /// </para>
  /// <list type="bullet">
  ///   <item><description><c>track0.bin</c> — written as the MBR / Track 0 record (sector count defaults to 63).</description></item>
  ///   <item><description><c>partition*.bin</c> (any name containing "partition") — written as compressed partition records.</description></item>
  ///   <item><description>any other entry — written as a partition (fallback so callers always get all bytes into the image).</description></item>
  /// </list>
  /// <para>
  /// The <see cref="FormatCreateOptions.MethodName"/> selects the compression
  /// mode: <c>stored</c>, <c>fastlz</c> (default), or <c>zlib-N</c> for N
  /// in 3..9. Passing a <see cref="FormatCreateOptions.Password"/>
  /// enables the CRC-16 stream cipher (the encryption flag at header
  /// byte 12, bit 1 is set).
  /// </para>
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var compression = MapMethodName(options.MethodName);
    using var w = new GhostWriter(output, compression, password: options.Password, leaveOpen: true);

    byte[]? track0 = null;
    var partitions = new List<byte[]>();

    foreach (var (name, data) in FlatFiles(inputs)) {
      if (name.Equals("track0.bin", StringComparison.OrdinalIgnoreCase) && track0 == null)
        track0 = data;
      else
        partitions.Add(data);
    }

    if (track0 != null)
      w.WriteTrack0(track0, sectors: 63);

    foreach (var p in partitions)
      w.WritePartition(p);

    w.WriteEnd();
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────

  /// <summary>
  /// True in-place append. Existing partition / track-0 / FEEF / compressed-
  /// block bytes stay byte-identical at their original offsets — only the
  /// trailing end-of-image record (type 0x0023) is overwritten and re-emitted
  /// at the new EOF. Synthetic reader-only entries (<c>metadata.ini</c>,
  /// <c>*.error.txt</c>) are filtered out so they never round-trip back as
  /// records. The encryption flag and compression method of the existing
  /// image are preserved; for encrypted images callers must use the
  /// <see cref="GhostInPlaceModifier.Add"/> overload that accepts a password.
  /// </summary>
  void IArchiveModifiable.Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var filtered = inputs.Where(i => !IsSyntheticInputName(i.ArchiveName)).ToList();
    GhostInPlaceModifier.Add(archive, filtered, password: null);
  }

  /// <summary>
  /// Appends a REMOVE annotation tombstone per entry name. Existing record
  /// bytes stay byte-identical at their original offsets; the modified
  /// reader interprets the tombstones and skips the named entries. Callers
  /// needing forensic deletion (no recoverable trace of the original bytes)
  /// must go through the rebuild-based <see cref="GhostModifier.Remove"/>.
  /// </summary>
  void IArchiveModifiable.Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      GhostInPlaceModifier.Remove(archive, name, password: null);
  }

  private static bool IsSyntheticInputName(string name)
    => name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
       || name.Equals("ghost-image.gho.bin", StringComparison.OrdinalIgnoreCase)
       || name.Equals("ghost-image.ghs.bin", StringComparison.OrdinalIgnoreCase)
       || name.Equals("dump-head.bin", StringComparison.OrdinalIgnoreCase)
       || name.Equals("dump-body.bin", StringComparison.OrdinalIgnoreCase)
       || name.EndsWith(".error.txt", StringComparison.OrdinalIgnoreCase);

  private static byte MapMethodName(string? name) => name?.ToLowerInvariant() switch {
    null or "" or "fastlz" or "fast" or "z1" => GhostConstants.CompressionFast,
    "stored" or "none" or "z0" => GhostConstants.CompressionNone,
    "zlib-3" or "z3" or "high-3" => GhostConstants.CompressionHigh3,
    "zlib-4" or "z4" or "high-4" => GhostConstants.CompressionHigh4,
    "zlib-5" or "z5" or "high-5" => GhostConstants.CompressionHigh5,
    "zlib-6" or "z6" or "high-6" => GhostConstants.CompressionHigh6,
    "zlib-7" or "z7" or "high-7" => GhostConstants.CompressionHigh7,
    "zlib-8" or "z8" or "high-8" => GhostConstants.CompressionHigh8,
    "zlib-9" or "z9" or "high-9" or "high" => GhostConstants.CompressionHigh9,
    _ => throw new InvalidDataException($"Ghost: unknown compression method '{name}'.")
  };
}
