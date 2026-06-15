#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Matroska;

/// <summary>
/// Surfaces a Matroska/WebM file as an archive: one entry per demuxed track,
/// plus attachments, plus chapters XML when present.
/// </summary>
public sealed class MkvFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFileInternalLayoutMap, IFileInternalChunkMover {
  public string Id => "Mkv";
  public string DisplayName => "MKV / WebM (demuxed)";
  public FormatCategory Category => FormatCategory.Video;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mkv";
  public IReadOnlyList<string> Extensions => [".mkv", ".webm", ".mka", ".mks"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x1A, 0x45, 0xDF, 0xA3], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Matroska / WebM container; tracks + attachments + chapters extractable.";

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

  private readonly MkvCuesFrontOptimizer _optimizer = new();

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => MkvLayoutMap.Enumerate(file);

  /// <inheritdoc />
  public void Optimize(Stream file) => _optimizer.Optimize(file);

  /// <inheritdoc />
  public void Optimize(Stream file, MetadataPlacementProfile? profile) => _optimizer.Optimize(file, profile);

  /// <summary>Maximum number of individual frame entries per video track.</summary>
  private const int MaxFrameEntries = 100_000;

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();
    var result = new MkvDemuxer().Demux(file);

    var entries = new List<(string, string, byte[])>();
    var audioMeta = new StringBuilder();
    var audioOrdinal = 0;
    foreach (var t in result.Tracks) {
      var ext = CodecToExtension(t.CodecId);
      var lang = string.IsNullOrEmpty(t.Language) ? "und" : t.Language;
      entries.Add(($"track_{t.Number:D2}_{t.TrackType}_{lang}{ext}", "Track", t.FrameBytes));

      // Emit individual video frames.
      if (t.TrackType == "video" && t.Frames.Count > 0) {
        var frameExt = CodecToFrameExtension(t.CodecId);
        var frameCount = Math.Min(t.Frames.Count, MaxFrameEntries);
        for (var f = 0; f < frameCount; ++f)
          entries.Add(($"frames/track_{t.Number:D2}/frame_{f + 1:D6}{frameExt}", "Frame", t.Frames[f].Data));
      }

      // Best-effort per-audio-track decode → one mono WAV per speaker (Kind Channel).
      // The raw track entry above is always kept; failures record a metadata reason.
      if (t.TrackType == "audio") {
        var decode = MkvAudioChannels.Decode(t);
        audioMeta.Append("track").Append(audioOrdinal).Append("_codec=").AppendLine(decode.Codec);
        if (decode.Channels != null)
          foreach (var ch in decode.Channels)
            entries.Add(($"TRACK{audioOrdinal}_{ch.Name}.wav", "Channel", ch.Wav));
        else if (decode.Reason != null)
          audioMeta.Append("track").Append(audioOrdinal).Append("_decode=").AppendLine(decode.Reason);
        ++audioOrdinal;
      }
    }
    if (audioMeta.Length > 0)
      entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(audioMeta.ToString())));
    foreach (var a in result.Attachments)
      entries.Add(($"attachments/{a.FileName}", "File", a.Data));
    if (result.ChaptersXml != null)
      entries.Add(("chapters.bin", "File", result.ChaptersXml));
    return entries;
  }

  private static string CodecToExtension(string codecId) => codecId switch {
    "V_MPEG4/ISO/AVC" => ".h264",
    "V_MPEGH/ISO/HEVC" => ".hevc",
    "V_VP9" => ".vp9",
    "V_VP8" => ".vp8",
    "V_AV1" => ".av1",
    "V_MJPEG" => ".mjpg",
    "A_AAC" => ".aac",
    "A_MPEG/L3" => ".mp3",
    "A_OPUS" => ".opus",
    "A_VORBIS" => ".ogg",
    "A_AC3" => ".ac3",
    "A_PCM/INT/LIT" => ".pcm",
    "A_FLAC" => ".flac",
    "S_TEXT/UTF8" => ".srt",
    "S_TEXT/ASS" => ".ass",
    "S_TEXT/SSA" => ".ssa",
    "S_VOBSUB" => ".sub",
    _ => ".bin",
  };

  /// <summary>Returns the appropriate extension for an individual video frame.</summary>
  private static string CodecToFrameExtension(string codecId) => codecId switch {
    "V_MPEG4/ISO/AVC" => ".h264",
    "V_MPEGH/ISO/HEVC" => ".hevc",
    "V_VP9" => ".vp9",
    "V_VP8" => ".vp8",
    "V_AV1" => ".av1",
    "V_MJPEG" => ".jpg",
    "V_MS/VFW/FOURCC" => ".bin",
    _ => ".bin",
  };
}
