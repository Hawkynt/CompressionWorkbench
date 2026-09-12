#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Matroska;

/// <summary>
/// Surfaces a Matroska/WebM file as an archive: one entry per demuxed track,
/// plus attachments, plus chapters XML when present. Single-audio-track files also
/// participate in the packet-preserving audio remux graph.
/// </summary>
public sealed class MkvFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract,
  IFileInternalLayoutMap, IFileInternalChunkMover, IAudioContainerFormat, IAudioDemuxSource, IAudioMuxTarget {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Mkv";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "MKV / WebM (demuxed)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Video;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".mkv";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".mkv", ".webm", ".mka", ".mks"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x1A, 0x45, 0xDF, 0xA3], Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored / packet-preserving mux")];
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
  public string Description => "Matroska / WebM container; tracks + attachments + chapters extractable, with encoded-audio packet mux/remux.";

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

  /// <summary>The option naming which of the two profiles a written file has to satisfy.</summary>
  private const string _PROFILE_OPTION = "Profile";

  /// <summary>The profile that restricts audio to what WebM permits.</summary>
  private const string _WEBM_PROFILE = "WebM";

  /// <summary>
  /// Gets the encoded audio codecs this writer can carry without re-encoding.
  /// </summary>
  /// <remarks>
  /// This is the Matroska set, because that is what the container can hold. WebM is a strict subset
  /// of Matroska that permits only Vorbis and Opus audio, and a caller who needs a file to satisfy
  /// it asks for that profile; the alternative -- one descriptor quietly enforcing the narrower rule
  /// on every <c>.mkv</c> as well -- would refuse files Matroska is specified to carry.
  /// </remarks>
  public IReadOnlyList<string> SupportedMuxCodecs => MkvAudioMuxer.SupportedCodecs;

  /// <summary>
  /// How long the frame at <paramref name="index"/> lasts, measured from when the next one starts.
  /// </summary>
  /// <remarks>
  /// The timestamps are in the segment's milliseconds, so the answer is converted to samples at the
  /// track's rate. A frame whose neighbours give nothing usable answers zero, and the caller refuses
  /// the stream rather than inventing a length.
  /// </remarks>
  private static long _DurationFromTimestamps(
    IReadOnlyList<MkvDemuxer.FrameEntry> frames, int index, int sampleRate) {
    if (sampleRate <= 0)
      return 0;

    var here = frames[index].TimestampTicks;
    var other = index + 1 < frames.Count ? frames[index + 1].TimestampTicks : -1;
    if (other < 0 && index > 0)
      other = here + (here - frames[index - 1].TimestampTicks);

    if (here < 0 || other < 0 || other <= here)
      return 0;

    return (other - here) * sampleRate / 1000;
  }

  /// <summary>The codecs the stated profile permits.</summary>
  public IReadOnlyList<string> SupportedMuxCodecsFor(FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return _IsWebmProfile(options) ? WebmAudioAdapter.SupportedCodecs : MkvAudioMuxer.SupportedCodecs;
  }

  /// <inheritdoc />
  /// <remarks>
  /// The profile narrows which codecs are accepted and nothing else. WebM is Matroska with a
  /// shorter list of permitted codecs, not a different file layout, so a WebM file is written by
  /// the same writer -- which is also what keeps <see cref="TryDemux"/> able to read back anything
  /// this descriptor produces, under either profile.
  /// </remarks>
  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(options);
    if (!MkvAudioMuxer.CanMux(stream, out reason))
      return false;

    if (!_IsWebmProfile(options))
      return true;

    if (WebmAudioAdapter.SupportedCodecs.Contains(
          MkvAudioMuxer.NormalizeCodecId(stream.CodecId), StringComparer.OrdinalIgnoreCase))
      return true;

    reason = $"WebM audio permits Opus and Vorbis, not '{stream.CodecId}'; Matroska carries it without the WebM profile.";
    return false;
  }

  /// <inheritdoc />
  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);

    MkvAudioMuxer.Mux(
      output,
      stream,
      _IsWebmProfile(options) ? MkvAudioMuxer.WebmDocType : MkvAudioMuxer.MatroskaDocType);
  }

  /// <summary>Whether the caller asked for a file that satisfies WebM rather than Matroska.</summary>
  private static bool _IsWebmProfile(FormatCreateOptions options)
    => string.Equals(
      options.GetOption(_PROFILE_OPTION, string.Empty), _WEBM_PROFILE, StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Exposes the single supported audio track as encoded packets. Multi-audio-track files remain
  /// available through the archive/demux surface because <see cref="AudioEncodedStream"/> represents
  /// one logical encoded stream and silently choosing one track would lose information.
  /// </summary>
  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    try {
      if (input.CanSeek)
        input.Position = 0;
      using var memory = new MemoryStream();
      input.CopyTo(memory);
      var tracks = new MkvDemuxer().Demux(memory.ToArray()).Tracks
        .Where(static track => track.TrackType == "audio")
        .Select(track => (Track: track, Codec: MkvAudioMuxer.CanonicalCodecId(track.CodecId)))
        .Where(static item => item.Codec is not null)
        .ToArray();

      if (tracks.Length != 1) {
        stream = null;
        return false;
      }

      var (track, codec) = tracks[0];
      if (!MkvAudioMuxer.SupportedCodecs.Contains(codec!, StringComparer.OrdinalIgnoreCase) ||
          track.AudioSampleRate <= 0 || track.AudioChannels <= 0 || track.Frames.Count == 0) {
        stream = null;
        return false;
      }

      var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["matroska-codec-id"] = track.CodecId,
      };
      if (!string.IsNullOrWhiteSpace(track.Language))
        properties["language"] = track.Language!;

      // Some codecs state their own duration in the packet -- Opus does, from its TOC byte. Vorbis
      // does not: nothing in a Vorbis packet says how long it lasts without the setup headers that
      // define the two block sizes. For those, the only thing that says is when the next block
      // starts, which is why the demuxer keeps each frame's timestamp. The last frame has no
      // successor, so it takes the one before it.
      var frames = track.Frames;
      var packets = new AudioPacket[frames.Count];
      for (var i = 0; i < frames.Count; ++i) {
        var duration = MkvAudioMuxer.InferDurationSamples(codec!, frames[i].Data);
        if (duration <= 0)
          duration = _DurationFromTimestamps(frames, i, track.AudioSampleRate);

        if (duration <= 0) {
          stream = null;
          return false;
        }

        packets[i] = new AudioPacket(frames[i].Data, DurationSamples: duration);
      }

      stream = new AudioEncodedStream(
        new AudioStreamFormat(codec!, track.AudioSampleRate, track.AudioChannels, track.AudioBitDepth, properties),
        packets,
        track.CodecPrivate);
      return true;
    } catch (InvalidDataException) {
      stream = null;
      return false;
    }
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
