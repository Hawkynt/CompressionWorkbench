#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DoubleSpace;

public sealed class DriveSpaceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "DriveSpace";
  public string DisplayName => "DriveSpace CVF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cvf";
  public IReadOnlyList<string> Extensions => [".cvf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(Encoding.ASCII.GetBytes("MSDSP6.2"), Offset: 3, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ds-lz77", "DS LZ77")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Microsoft DriveSpace compressed volume file (MS-DOS 6.22+/Windows 95).
  /// <para>
  /// Spec-compliant MDBPB + MDFAT + BitFAT + DATA layout. Inner FAT16 volume
  /// with VFAT long filenames. Writer emits stored (uncompressed) runs; the
  /// JM/DSS LZ payload variant is a future enhancement.
  /// </para>
  /// </summary>
  public string Description => "Microsoft DriveSpace compressed volume file MS-DOS 6.22+/Windows 95 (MDBPB/MDFAT/BitFAT layout; stored runs, VFAT LFN)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DoubleSpaceReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "DS-LZ77", e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DoubleSpaceReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new DoubleSpaceWriter { Variant = CvfVariant.DriveSpace62 };
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
