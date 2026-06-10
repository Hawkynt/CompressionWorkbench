#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cdi;

public sealed class CdiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "CDI is a DiscJuggler disc image (sector-based ISO 9660 + footer) — defragmentation isn't meaningful.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  public string Id => "Cdi";
  public string DisplayName => "CDI";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cdi";
  public IReadOnlyList<string> Extensions => [".cdi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // CDI version identifiers (0x80000004/5/6) appear at a variable offset from EOF,
  // making fixed-offset header magic impractical; detection relies on extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DiscJuggler CDI disc image (R/W via in-place sector rewrite at fixed offsets; inner ISO 9660 directory mutation delegated to FileSystem.Iso; multi-track DAOI layouts deferred)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CdiReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CdiReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: ISO 9660 image followed by a CDI v2 footer. The reader only uses
    // the footer for version detection; the session-descriptor offset isn't
    // dereferenced for ISO extraction.
    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);
    output.Write(iso.Build());
    // Footer: uint32 LE version (CDI v2 = 0x80000004) + uint32 LE offset-from-EOF.
    Span<byte> footer = stackalloc byte[8];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer, 0x80000004);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], 0);
    output.Write(footer);
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────

  /// <summary>
  /// Rewrites raw CD sectors in place. Inputs whose <c>ArchiveName</c> matches
  /// <c>sector-NNNNNN.bin</c> are written at the fixed byte offset
  /// <c>lba * sectorSize + dataOffset</c>; everything outside the touched
  /// 2 048-byte user-data region — including the 8-byte CDI footer — stays
  /// byte-identical (the footer migrates with the new EOF when the data area
  /// grows past the previous end).
  ///
  /// <para>Inputs not matching the synthetic sector schema are skipped —
  /// inner-ISO 9660 directory mutation is delegated to <c>FileSystem.Iso</c>
  /// and is out of scope for the sector-rewrite modifier.</para>
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    CdiInPlaceModifier.AddOrReplaceSectors(archive,
      inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i.ReadContent())));
  }

  /// <summary>
  /// Zeros the 2 048-byte user-data region of each named sector. Sector
  /// framing bytes (sync / address / mode / EDC) on raw geometries and the
  /// trailing CDI footer are preserved so the LBA-to-offset map and the rest
  /// of the image remain byte-identical.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    CdiInPlaceModifier.RemoveSectors(archive, entryNames);
  }
}
