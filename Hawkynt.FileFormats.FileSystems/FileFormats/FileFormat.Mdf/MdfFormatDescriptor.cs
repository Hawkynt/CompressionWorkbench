#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mdf;

/// <summary>
/// Alcohol 120% MDF/MDS optical-disc image: sector data in <c>.mdf</c> plus
/// session/track metadata in the companion <c>.mds</c> descriptor.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-119/</c> — ISO 9660 / ECMA-119 filesystem layout</description></item>
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-130/</c> — CD-ROM Mode 1 sector framing and EDC/ECC</description></item>
///   <item><description><c>https://cdemu.sourceforge.io</c> — CDEmu/libMirage MDS/MDF implementation used as a behavioral reference</description></item>
///   <item><description><c>https://github.com/aaru-dps/Aaru/tree/devel/Aaru.Images/Alcohol120</c> — LGPL-2.1-or-later Alcohol 120% implementation used to cross-check MDS structures and track modes</description></item>
/// </list>
/// </summary>
public sealed class MdfFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveLayoutMap,
  IArchivePurgeable {

  private const int StandaloneEditReserveSectors = 32;

  public string Id => "Mdf";
  public string DisplayName => "MDF/MDS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".mdf";
  public IReadOnlyList<string> Extensions => [".mdf", ".mds"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // MDF itself has no header; the companion MDS does.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MEDIA DESCRIPTOR"u8.ToArray(), Offset: 0, Confidence: 0.99),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Alcohol 120% MDF/MDS optical image; ISO 9660 content is editable inside the existing MDF track capacity, " +
    "with raw-sector EDC/ECC regenerated and the physical sector count kept stable so companion MDS geometry remains valid";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new MdfReader(stream, leaveOpen: true);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index,
      entry.FullPath,
      entry.Size,
      entry.Size,
      "iso9660",
      entry.IsDirectory,
      false,
      null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new MdfReader(stream, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files != null && !MatchesFilter(entry.FullPath, files)) continue;
      WriteFile(outputDir, entry.FullPath, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Creates a standalone MDF data stream as 2 048-byte cooked ISO sectors.
  /// The archive API owns one output stream and therefore cannot emit the MDS
  /// sidecar. A small physical tail reserve is left outside ISO's declared
  /// volume-space count so a freshly-created image can exercise genuine add /
  /// replace semantics without resizing; existing paired images are never grown.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);
    output.Write(iso.Build());
    output.Write(new byte[StandaloneEditReserveSectors * MdfInPlaceModifier.Iso9660SectorSize]);
  }

  /// <summary>
  /// Adds or replaces root-level ISO 9660 files inside the existing MDF track.
  /// The edit is staged transactionally and committed only if the result parses.
  /// Physical growth is refused because that would require changing the MDS
  /// track descriptors, which are outside the single-stream mutation contract.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs))
      MdfIsoOperations.AddOrReplace(archive, name, data);
  }

  /// <summary>
  /// Removes root-level ISO 9660 files and wipes their former data sectors while
  /// preserving the MDF's physical sector count and raw framing.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      MdfIsoOperations.Remove(archive, name);
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive)
    => MdfLayoutMap.Enumerate(archive);

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => MdfIsoOperations.WipeUnusedSpace(image, wipeClusterTips, wipeDeletedEntries);

  /// <inheritdoc />
  public void Purge(Stream archive)
    => MdfIsoOperations.Purge(archive);

  /// <inheritdoc />
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <inheritdoc />
  public void Defragment(Stream archive, DefragOptions options)
    => MdfIsoOperations.Defragment(archive, options);
}
