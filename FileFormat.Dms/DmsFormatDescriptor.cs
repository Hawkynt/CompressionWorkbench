#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dms;

public sealed class DmsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Dms";
  public string DisplayName => "DMS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dms";
  public IReadOnlyList<string> Extensions => [".dms"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'D', (byte)'M', (byte)'S', (byte)'!'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dms", "DMS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga Disk Masher System, floppy disk archiver";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DmsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, $"track_{e.TrackNumber:D3}.bin",
      e.UncompressedSize, e.CompressedSize, $"Mode {e.CompressionMode}", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DmsReader(stream);
    var disk = r.ExtractDisk();
    WriteFile(outputDir, "disk.adf", disk);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileInputs = inputs.Where(i => !i.IsDirectory).ToArray();
    if (fileInputs.Length != 1)
      throw new ArgumentException("DMS format requires exactly one input file (disk image).");
    var data = File.ReadAllBytes(fileInputs[0].FullPath);
    using var w = new DmsWriter(output, leaveOpen: true);
    w.WriteDisk(data, compressionMode: 0);
  }
}
