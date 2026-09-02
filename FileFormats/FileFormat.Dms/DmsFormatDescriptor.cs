#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dms;

/// <summary>
/// Amiga Disk Masher System (DMS) — track-based floppy-disk archiver ubiquitous in the Amiga scene.
///
/// References:
/// <list type="bullet">
///   <item><description>xDMS by Andre R. de la Rocha — open-source DMS extractor and de-facto format reference</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Disk_Masher_System</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class DmsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  /// <summary>Rebuild-based defrag: extracts the disk image then re-emits the DMS file.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts the disk image then re-emits the DMS file.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new DmsReader(stream);
        var disk = r.ExtractDisk();
        return [("disk.adf", disk)];
      },
      buildImage: files => {
        if (files.Count == 0)
          throw new InvalidOperationException("DMS defrag requires a disk image to be present.");
        using var ms = new MemoryStream();
        using (var w = new DmsWriter(ms, leaveOpen: true))
          w.WriteDisk(files[0].Data, compressionMode: 0);
        return ms.ToArray();
      });
  }
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dms";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DMS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".dms";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dms"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'D', (byte)'M', (byte)'S', (byte)'!'], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dms", "DMS")];
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
  public string Description => "Amiga Disk Masher System, floppy disk archiver";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DmsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, $"track_{e.TrackNumber:D3}.bin",
      e.UncompressedSize, e.CompressedSize, $"Mode {e.CompressionMode}", false, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DmsReader(stream);
    var disk = r.ExtractDisk();
    WriteFile(outputDir, "disk.adf", disk);
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileInputs = inputs.Where(i => !i.IsDirectory).ToArray();
    if (fileInputs.Length != 1)
      throw new ArgumentException("DMS format requires exactly one input file (disk image).");
    var data = fileInputs[0].ReadContent();
    using var w = new DmsWriter(output, leaveOpen: true);
    w.WriteDisk(data, compressionMode: 0);
  }
}
