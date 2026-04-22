#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Mp4;

/// <summary>
/// Exposes an MP4/MOV file as an archive of demuxed tracks. Video tracks produce
/// raw H.264 Annex-B (or raw sample data for non-H.264 codecs); audio tracks
/// produce the concatenated sample payload in track order. Not a re-muxer — the
/// output is elementary streams, not playable MP4 fragments.
/// </summary>
public sealed class Mp4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Mp4";
  public string DisplayName => "MP4 / MOV (demuxed)";
  public FormatCategory Category => FormatCategory.Video;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mp4";
  public IReadOnlyList<string> Extensions => [".mp4", ".m4v", ".m4a", ".mov", ".3gp", ".3g2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ftyp"u8.ToArray(), Offset: 4, Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MP4/MOV container; each track extractable as an elementary stream.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();
    var demuxer = new Mp4Demuxer();
    var tracks = demuxer.Demux(file);

    var entries = new List<(string, string, byte[])>();
    foreach (var t in tracks) {
      var ext = ChooseExtension(t.HandlerType, t.CodecFourCc);
      var name = $"track_{t.Id:D2}_{t.HandlerType}_{t.CodecFourCc}{ext}";
      entries.Add((name, t.HandlerType == "vide" ? "Track" : t.HandlerType == "soun" ? "Track" : "Track", t.Data));
    }
    return entries;
  }

  private static string ChooseExtension(string handlerType, string codec) => (handlerType, codec) switch {
    ("vide", "avc1") => ".h264",
    ("vide", "avc3") => ".h264",
    ("vide", "hvc1") => ".hevc",
    ("vide", "hev1") => ".hevc",
    ("vide", _) => ".bin",
    ("soun", "mp4a") => ".aac",
    ("soun", _) => ".bin",
    ("subt", _) => ".srt",
    ("text", _) => ".txt",
    _ => ".bin",
  };
}
