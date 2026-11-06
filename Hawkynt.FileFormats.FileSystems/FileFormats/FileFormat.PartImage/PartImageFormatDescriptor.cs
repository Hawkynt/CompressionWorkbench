#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PartImage;

/// <summary>
/// partimage partition image (<c>.partimg</c>, often split as <c>name.000</c>, <c>name.001</c>, …;
/// plain, gzip or bzip2). Lists <c>metadata.ini</c> (volume, main and local header fields) and
/// <c>partition.img</c>, the restored partition: used blocks from the image, unused blocks as zeros.
/// Read-only; every 64 KiB of blocks is checked against its CRC before it is written out.
/// </summary>
public sealed class PartImageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "PartImage";
  public string DisplayName => "partimage";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".partimg";
  public IReadOnlyList<string> Extensions => [".partimg"];
  public IReadOnlyList<string> CompoundExtensions => [".partimg.gz", ".partimg.bz2"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("PaRtImAgE-VoLuMe"u8.ToArray(), Confidence: 0.98)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("none", "Uncompressed"), new("gzip", "gzip"), new("bzip2", "bzip2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "partimage partition image (used blocks, bitmap and CRC checks; plain, gzip or bzip2)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var info = PartImageReader.Open(stream).ReadHeaders();
      var ini = Encoding.UTF8.GetBytes(info.Render());
      var method = info.Compression switch { 1 => "gzip", 2 => "bzip2", _ => "none" };
      return [
        new(0, "metadata.ini", ini.Length, ini.Length, "stored", false, false, null),
        new(1, "partition.img", (long)info.ImageSize, stream.CanSeek ? stream.Length : 0, method, false, false, null),
      ];
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.partimg", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = PartImageReader.Open(stream);
    var info = reader.ReadHeaders();
    if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(info.Render()));
    if (files is { Length: > 0 } && !MatchesFilter("partition.img", files)) return;
    Directory.CreateDirectory(outputDir);
    var target = Path.Combine(outputDir, "partition.img");
    try {
      using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
      reader.CopyPartition(info, output);
    } catch {
      // Nothing unverified stays behind.
      File.Delete(target);
      throw;
    }
  }
}
