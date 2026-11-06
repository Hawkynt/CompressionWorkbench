#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Uif;

/// <summary>
/// MagicISO UIF (Universal Image Format) disc image. The <c>bbis</c> signature sits in the trailer, not at
/// the start, so the image is found by its extension. Lists <c>metadata.ini</c> and <c>image.iso</c>.
/// Read-only: no free tool writes UIF.
/// </summary>
public sealed class UifFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Uif";
  public string DisplayName => "MagicISO UIF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".uif";
  public IReadOnlyList<string> Extensions => [".uif"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib-compressed sector runs")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MagicISO UIF disc image (bbis trailer, blhr block table, zlib sector runs)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var image = UifImage.Read(Seekable(stream));
      var ini = Encoding.UTF8.GetBytes(image.RenderMetadata());
      return [
        new(0, "metadata.ini", ini.Length, ini.Length, "stored", false, false, null),
        new(1, "image.iso", (long)image.Length, stream.Length, "zlib", false, false, null),
      ];
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.uif", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var source = Seekable(stream);
    var image = UifImage.Read(source);
    if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(image.RenderMetadata()));
    if (files is { Length: > 0 } && !MatchesFilter("image.iso", files)) return;
    Directory.CreateDirectory(outputDir);
    var target = Path.Combine(outputDir, "image.iso");
    try {
      using var output = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
      image.CopyTo(source, output);
    } catch {
      File.Delete(target);
      throw;
    }
  }

  private static Stream Seekable(Stream stream) {
    if (stream.CanSeek) return stream;
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms;
  }
}
