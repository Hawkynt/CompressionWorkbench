#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Crx;

public sealed class CrxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Crx";
  public string DisplayName => "CRX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".crx";
  public IReadOnlyList<string> Extensions => [".crx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'C', (byte)'r', (byte)'2', (byte)'4'], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Chrome extension package (CRX3 header + ZIP)";

  private static Stream StripCrxHeader(Stream stream) {
    var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    var magic = reader.ReadBytes(4);
    if (magic is not [(byte)'C', (byte)'r', (byte)'2', (byte)'4'])
      throw new InvalidDataException("Not a CRX file.");
    var version = reader.ReadUInt32();
    var headerLen = reader.ReadUInt32();
    stream.Position = 12 + headerLen;
    return stream;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    StripCrxHeader(stream);
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    StripCrxHeader(stream);
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }
}
