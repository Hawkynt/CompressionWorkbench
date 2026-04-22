#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Webp;

/// <summary>
/// Exposes a WebP file as an archive: <c>FULL.webp</c> always, plus per-frame
/// <c>frame_NN.webp</c> for animated WebPs (VP8X + ANMF chunks). Metadata chunks
/// (EXIF / XMP / ICCP) surface under <c>metadata/</c>.
/// </summary>
public sealed class WebpFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Webp";
  public string DisplayName => "WebP";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".webp";
  public IReadOnlyList<string> Extensions => [".webp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "RIFF" at 0 + "WEBP" at 8. Match the 4-byte "WEBP" at offset 8 for a tighter fit.
    new("WEBP"u8.ToArray(), Offset: 8, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "VP8/VP8L")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "WebP image container; animated frames extractable.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var blob = ReadAll(stream);
    return BuildEntries(blob)
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.Name,
        OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
        Kind: e.Kind))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var blob = ReadAll(stream);
    foreach (var e in BuildEntries(blob)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(byte[] blob) {
    var entries = new List<(string, string, byte[])> {
      ("FULL.webp", "Track", blob),
    };

    var reader = new WebpReader(blob);
    var frameIndex = 0;
    foreach (var chunk in reader.Chunks) {
      switch (chunk.FourCc) {
        case "ANMF":
          // Animation frame. Body: 16-byte ANMF header + VP8/VP8L sub-chunk.
          // Rebuild a standalone still WebP wrapping that sub-chunk so the extracted
          // bytes open in any viewer.
          var body = reader.ReadBody(chunk);
          if (body.Length > 16) {
            var sub = body.AsSpan(16).ToArray();
            entries.Add(($"frame_{frameIndex:D3}.webp", "Frame", WrapAsWebp(sub)));
            ++frameIndex;
          }
          break;
        case "EXIF":
          entries.Add(("metadata/exif.bin", "Tag", reader.ReadBody(chunk)));
          break;
        case "XMP ":
          entries.Add(("metadata/xmp.xml", "Tag", reader.ReadBody(chunk)));
          break;
        case "ICCP":
          entries.Add(("metadata/icc.bin", "Tag", reader.ReadBody(chunk)));
          break;
      }
    }
    return entries;
  }

  // Wraps a VP8/VP8L/VP8X sub-chunk as a standalone RIFF/WEBP file.
  private static byte[] WrapAsWebp(byte[] vp8Body) {
    using var ms = new MemoryStream();
    ms.Write("RIFF"u8);
    Span<byte> sz = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)(4 + vp8Body.Length));
    ms.Write(sz);
    ms.Write("WEBP"u8);
    ms.Write(vp8Body);
    return ms.ToArray();
  }
}
