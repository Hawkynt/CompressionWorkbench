#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Mp4;

/// <summary>
/// Exposes an MP4/MOV file as an archive of demuxed tracks. Video tracks produce
/// raw H.264 Annex-B (or raw sample data for non-H.264 codecs); audio tracks
/// produce the concatenated sample payload in track order. Not a re-muxer — the
/// output is elementary streams, not playable MP4 fragments.
/// </summary>
public sealed class Mp4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFileInternalLayoutMap, IFileInternalChunkMover {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Mp4";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MP4 / MOV (demuxed)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Video;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".mp4";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".mp4", ".m4v", ".m4a", ".mov", ".3gp", ".3g2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ftyp"u8.ToArray(), Offset: 4, Confidence: 0.9),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
public string Description => "MP4/MOV container; each track extractable as an elementary stream.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  /// <summary>Maximum number of individual frame entries per video track.</summary>
  private const int MaxFrameEntries = 100_000;

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
      entries.Add((name, "Track", t.Data));

      // Emit individual video frames.
      if (t.HandlerType == "vide" && t.Samples.Count > 0) {
        var frameExt = ChooseFrameExtension(t.CodecFourCc);
        var frameCount = Math.Min(t.Samples.Count, MaxFrameEntries);
        for (var f = 0; f < frameCount; ++f)
          entries.Add(($"frames/track_{t.Id:D2}/frame_{f + 1:D6}{frameExt}", "Frame", t.Samples[f].Data));
      }
    }

    // Best-effort per-audio-track decode → one mono WAV per speaker (Kind Channel).
    // Audio traks keep their raw concatenated-sample entry above; here we add the
    // decoded channels plus a metadata.ini note. Failures fall back to raw-only.
    var audioTracks = Mp4AudioChannels.Decode(file);
    if (audioTracks.Count > 0) {
      var meta = new StringBuilder();
      foreach (var at in audioTracks) {
        meta.Append("track").Append(at.TrackId).Append("_codec=").AppendLine(at.Codec);
        if (at.Channels != null)
          foreach (var ch in at.Channels)
            entries.Add(($"TRACK{at.TrackId}_{ch.Name}.wav", "Channel", ch.Wav));
        else if (at.Reason != null)
          meta.Append("track").Append(at.TrackId).Append("_decode=").AppendLine(at.Reason);
      }
      entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
    }
    return entries;
  }

  private static string ChooseExtension(string handlerType, string codec) => (handlerType, codec) switch {
    ("vide", "avc1") => ".h264",
    ("vide", "avc3") => ".h264",
    ("vide", "hvc1") => ".hevc",
    ("vide", "hev1") => ".hevc",
    ("vide", "mp4v") => ".m4v",
    ("vide", "mjpa") or ("vide", "mjpb") => ".mjpg",
    ("vide", _) => ".bin",
    ("soun", "mp4a") => ".aac",
    ("soun", _) => ".bin",
    ("subt", _) => ".srt",
    ("text", _) => ".txt",
    _ => ".bin",
  };

  /// <summary>Returns the appropriate extension for an individual video frame.</summary>
  private static string ChooseFrameExtension(string codec) => codec switch {
    "avc1" or "avc3" => ".h264",
    "hvc1" or "hev1" => ".hevc",
    "mjpa" or "mjpb" => ".jpg",
    _ => ".bin",
  };

  private readonly Mp4LayoutMap _layoutMap = new();
  private readonly Mp4FastStart _fastStart = new();

  /// <summary>
  /// Enumerates the chunks.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => this._layoutMap.EnumerateChunks(file);
  /// <summary>
  /// Performs the optimize operation.
  /// </summary>
public void Optimize(Stream file) => this._fastStart.Optimize(file);
  /// <summary>
  /// Performs the optimize operation.
  /// </summary>
public void Optimize(Stream file, MetadataPlacementProfile? profile) => this._fastStart.Optimize(file, profile);
}
