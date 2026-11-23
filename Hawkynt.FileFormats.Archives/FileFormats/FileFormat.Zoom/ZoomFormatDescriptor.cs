#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zoom;

/// <summary>
/// Amiga Zoom disk archiver (".zom", magic "ZOM5" or the older "ZOOM"): an 880 KiB floppy packed
/// cylinder by cylinder with LZHUF and run-length coding. Read-only: <c>disk.adf</c> is the floppy image,
/// <c>text.txt</c> the attached text when there is one.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — libxad's Zoom client (<c>libxad/clients/Zoom.c</c>)</description></item>
///   <item><description><c>https://github.com/horsicq/XArchive</c> — <c>Algos/xzoomdecoder.cpp</c></description></item>
/// </list>
/// </summary>
public sealed class ZoomFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string ImageName = "disk.adf";
  private const string TextName = "text.txt";

  public string Id => "Zoom";
  public string DisplayName => "Zoom (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".zom";
  public IReadOnlyList<string> Extensions => [".zom"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ZOM5"u8.ToArray(), Confidence: 0.95),
    new("ZOOM"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zoom", "LZHUF + RLE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga Zoom floppy disk archive (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new ZoomReader(stream);
      var entries = new List<ArchiveEntryInfo> {
        new(0, ImageName, ZoomReader.Cylinders * ZoomReader.CylinderSize, stream.CanSeek ? stream.Length : 0,
          r.IsOldFormat ? "Zoom (old)" : "Zoom", false, r.IsEncrypted, null),
      };
      if (r.Text is { } text)
        entries.Add(new(1, TextName, text.Length, text.Length, "LZHUF", false, false, null));
      return entries;
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.zom", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZoomReader(stream);
    if (files is not { Length: > 0 } || MatchesFilter(ImageName, files))
      WriteFile(outputDir, ImageName, r.ExtractDisk());
    if (r.Text is { } text && (files is not { Length: > 0 } || MatchesFilter(TextName, files)))
      WriteFile(outputDir, TextName, text);
  }
}
