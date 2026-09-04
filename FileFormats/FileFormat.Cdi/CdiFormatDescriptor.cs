#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cdi;

/// <summary>
/// DiscJuggler CDI disc image (Padus) — track data plus trailing session/track descriptor blocks.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/DiscJuggler</c> — background on the creating tool</description></item>
///   <item><description>CDIrip source — the DiscJuggler layout was reverse-engineered by the disc-preservation community; Padus never published a spec</description></item>
/// </list>
/// </summary>
public sealed class CdiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {


  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Cdi";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "CDI";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".cdi";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".cdi"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // CDI version identifiers (0x80000004/5/6) appear at a variable offset from EOF,
  // making fixed-offset header magic impractical; detection relies on extension.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "DiscJuggler CDI disc image (R/W via in-place sector rewrite at fixed offsets; inner ISO 9660 directory mutation delegated to FileSystem.Iso; multi-track DAOI layouts deferred)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CdiReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CdiReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
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
