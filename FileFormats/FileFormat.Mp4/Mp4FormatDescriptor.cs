#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Aac;
using Compression.Registry;

namespace FileFormat.Mp4;

/// <summary>
/// MP4/MOV demux surface plus an audio-only M4A write path. The writer currently
/// accepts AAC-LC access units directly or canonical PCM16 that can be encoded to AAC-LC.
/// </summary>
public sealed class Mp4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IFileInternalLayoutMap, IFileInternalChunkMover,
  IAudioContainerFormat, IAudioMuxTarget, IAudioPcmTarget {

  private static readonly string[] AacCodecs = ["aac", "aac-lc"];

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
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aac", "AAC-LC audio / M4A")];
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
  public string Description => "MP4/MOV container; demuxed tracks plus audio-only AAC-LC M4A muxing.";

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
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (!e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) continue;
      output.Write(e.Data);
      return;
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  public IReadOnlyList<string> SupportedMuxCodecs => ["aac"];

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    if (!stream.CodecId.Equals("aac", StringComparison.OrdinalIgnoreCase)) {
      reason = "the audio-only MP4 writer currently accepts AAC access units";
      return false;
    }
    if (stream.SampleRate <= 0 || stream.Channels is < 1 or > 2) {
      reason = "AAC M4A muxing requires mono/stereo with a positive sample rate";
      return false;
    }
    reason = null;
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);
    output.Write(Mp4AudioMuxer.MuxAac(stream));
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => AacCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!AacCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not supported by the M4A writer";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "M4A AAC encoding requires signed PCM16 input";
      return false;
    }
    if (format.Channels is < 1 or > 2) {
      reason = "the current AAC-LC encoder supports mono or stereo";
      return false;
    }
    if (Array.IndexOf(AacAdtsReader.SampleRateTable, format.SampleRate) is < 0 or > 12) {
      reason = "sample rate is not an AAC/ADTS standard rate";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    var samples = new short[pcm.InterleavedData.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.InterleavedData.AsSpan(i * 2, 2));

    var bitrate = options.TryGetInt("bitrate", out var configuredBitrate)
      ? configuredBitrate
      : pcm.Format.Channels == 1 ? 64_000 : 128_000;
    var cutoff = options.TryGetInt("cutoff", out var configuredCutoff) ? configuredCutoff : 0;
    var window = options.GetString("window")?.ToLowerInvariant() switch {
      "kbd" => AacEncoderWindowShape.Kbd,
      _ => AacEncoderWindowShape.Sine,
    };
    var stereoMode = options.GetString("stereo-mode")?.ToLowerInvariant() switch {
      "independent" => AacStereoCodingMode.Independent,
      "ms" or "mid-side" or "midside" => AacStereoCodingMode.MidSide,
      _ => AacStereoCodingMode.Auto,
    };

    var adts = AacEncoder.Encode(samples, new AacEncoderOptions(
      pcm.Format.SampleRate, pcm.Format.Channels, bitrate, cutoff, window, stereoMode));
    var encoded = DemuxAdts(adts);
    this.Mux(output, encoded, options);
  }

  private static AudioEncodedStream DemuxAdts(byte[] adts) {
    var packets = new List<AudioPacket>();
    AdtsHeader? first = null;
    var offset = 0;
    while (offset + AacAdtsReader.ShortHeaderLength <= adts.Length) {
      var header = AacAdtsReader.ParseHeader(adts, offset);
      if (header.FrameLength < header.HeaderLengthBytes || offset + header.FrameLength > adts.Length)
        throw new InvalidDataException("Encoded ADTS frame overruns buffer.");
      first ??= header;
      packets.Add(new AudioPacket(
        adts.AsSpan(offset + header.HeaderLengthBytes, header.FrameLength - header.HeaderLengthBytes).ToArray(),
        (header.NumberOfRawDataBlocks + 1L) * AacEncoder.FrameSamples));
      offset += header.FrameLength;
    }
    if (first is not { } initial || packets.Count == 0 || offset != adts.Length)
      throw new InvalidDataException("AAC encoder produced an incomplete ADTS stream.");

    var objectType = initial.Profile + 1;
    var asc = new byte[2];
    asc[0] = (byte)((objectType << 3) | (initial.SampleRateIndex >> 1));
    asc[1] = (byte)(((initial.SampleRateIndex & 1) << 7) | (initial.ChannelConfiguration << 3));
    return new AudioEncodedStream(
      new AudioStreamFormat("aac", initial.SampleRate, initial.ChannelConfiguration),
      packets,
      asc);
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

      if (t.HandlerType == "vide" && t.Samples.Count > 0) {
        var frameExt = ChooseFrameExtension(t.CodecFourCc);
        var frameCount = Math.Min(t.Samples.Count, MaxFrameEntries);
        for (var f = 0; f < frameCount; ++f)
          entries.Add(($"frames/track_{t.Id:D2}/frame_{f + 1:D6}{frameExt}", "Frame", t.Samples[f].Data));
      }
    }

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
    ("vide", "avc1") or ("vide", "avc3") => ".h264",
    ("vide", "hvc1") or ("vide", "hev1") => ".hevc",
    ("vide", "mp4v") => ".m4v",
    ("vide", "mjpa") or ("vide", "mjpb") => ".mjpg",
    ("vide", _) => ".bin",
    ("soun", "mp4a") => ".aac",
    ("soun", _) => ".bin",
    ("subt", _) => ".srt",
    ("text", _) => ".txt",
    _ => ".bin",
  };

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
