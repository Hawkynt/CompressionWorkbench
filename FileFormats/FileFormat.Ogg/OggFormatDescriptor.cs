#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Opus;
using Codec.Pcm;
using Codec.Speex;
using Codec.Vorbis;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Ogg;

/// <summary>
/// Ogg container with packet inspection, PCM decode, and managed Vorbis/Opus creation.
/// </summary>
public sealed class OggFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IFileInternalLayoutMap, IArchiveCreatable, IArchiveWriteConstraints,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget {

  private static readonly string[] EncodeCodecs = ["vorbis", "opus"];

  public string Id => "Ogg";
  public string DisplayName => "OGG (Xiph container)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ogg";
  public IReadOnlyList<string> Extensions => [".ogg", ".oga", ".opus", ".spx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("OggS"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("vorbis", "Ogg Vorbis"),
    new("opus", "Ogg Opus"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Ogg bitstream; packet inspection plus Vorbis/Opus read/write and per-channel PCM.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Channel" ? "pcm" : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(e.Name, files)) continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (!e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) continue;
      output.Write(e.Data);
      return;
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => OggLayoutMap.Enumerate(file);

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "Ogg accepts FULL.ogg or 1-8 mono PCM16 WAV channels; method/codec selects vorbis or opus.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.ogg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"not an Ogg input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.ogg", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var channels = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .Select(static file => new WavReader().Read(file.Data))
      .ToArray();
    if (channels.Length is < 1 or > 8)
      throw new InvalidOperationException("Ogg creation requires 1-8 mono WAV channels.");
    var first = channels[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample != 16)
      throw new InvalidOperationException("Ogg Vorbis/Opus creation requires PCM16 WAV input.");
    if (channels.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1 ||
                                channel.BitsPerSample != 16 || channel.SampleRate != first.SampleRate ||
                                channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All Ogg channel WAVs must be PCM16 with matching rate and frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), 16);
    var codec = options.Method ?? options.GetString("codec") ?? "vorbis";
    this.EncodePcm(output,
      new AudioPcmBuffer(new AudioPcmFormat(first.SampleRate, channels.Length, 16), interleaved),
      codec, options);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!EncodeCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"unsupported Ogg codec '{codecId}'";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "managed Vorbis/Opus encoders currently accept signed PCM16";
      return false;
    }
    if (format.Channels is < 1 or > 8 || format.SampleRate <= 0) {
      reason = "Ogg audio creation supports 1-8 channels with a positive sample rate";
      return false;
    }
    if (codecId.Equals("opus", StringComparison.OrdinalIgnoreCase) &&
        format.SampleRate is not (8000 or 12000 or 16000 or 24000 or 48000)) {
      reason = "Opus input rate must be 8, 12, 16, 24 or 48 kHz";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    var samples = ToShorts(pcm.InterleavedData);

    if (codecId.Equals("vorbis", StringComparison.OrdinalIgnoreCase)) {
      var quality = 0.5f;
      var text = options.GetString("quality");
      if (text is not null && !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out quality))
        throw new ArgumentException($"Invalid Vorbis quality '{text}'.", nameof(options));
      var comments = ReadComments(options);
      output.Write(VorbisEncoder.Encode(samples,
        new VorbisEncoderOptions(pcm.Format.SampleRate, pcm.Format.Channels, quality, Comments: comments)));
      return;
    }

    var bitrate = options.TryGetInt("bitrate", out var configuredBitrate) ? configuredBitrate : 128_000;
    var complexity = options.TryGetInt("complexity", out var configuredComplexity) ? configuredComplexity : 10;
    var vbr = options.GetString("vbr") is { } vbrText ? bool.Parse(vbrText) : true;
    var constrainedVbr = options.GetString("constrained-vbr") is { } cvbrText && bool.Parse(cvbrText);
    var dtx = options.GetString("dtx") is { } dtxText && bool.Parse(dtxText);
    var fec = options.GetString("fec") is { } fecText && bool.Parse(fecText);
    var loss = options.TryGetInt("packet-loss-percent", out var configuredLoss) ? configuredLoss : 0;
    var duration = options.GetString("frame-ms") is { } frameText
      ? double.Parse(frameText, CultureInfo.InvariantCulture)
      : 20.0;
    output.Write(OpusCodec.Encode(samples, new OpusEncoderOptions(
      pcm.Format.SampleRate,
      pcm.Format.Channels,
      Bitrate: bitrate,
      Complexity: complexity,
      UseVbr: vbr,
      ConstrainedVbr: constrainedVbr,
      UseDtx: dtx,
      UseInbandFec: fec,
      PacketLossPercent: loss,
      FrameDurationMilliseconds: duration)));
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    var blob = ReadAll(input);
    var isOpus = IndexOf(blob, "OpusHead"u8) >= 0;
    var isSpeex = !isOpus && IndexOf(blob, "Speex   "u8) >= 0;
    int channels;
    int sampleRate;
    using var info = new MemoryStream(blob, writable: false);
    using var source = new MemoryStream(blob, writable: false);
    using var pcm = new MemoryStream();
    if (isOpus) {
      var metadata = OpusCodec.ReadStreamInfo(info);
      channels = metadata.Channels;
      sampleRate = metadata.SampleRate > 0 ? metadata.SampleRate : 48_000;
      OpusCodec.Decompress(source, pcm);
    } else if (isSpeex) {
      var metadata = SpeexCodec.ReadStreamInfo(info);
      channels = metadata.Channels;
      sampleRate = metadata.SampleRate;
      SpeexCodec.Decompress(source, pcm);
    } else {
      var metadata = VorbisCodec.ReadStreamInfo(info);
      channels = metadata.Channels;
      sampleRate = metadata.SampleRate;
      VorbisCodec.Decompress(source, pcm);
    }
    return new AudioPcmBuffer(new AudioPcmFormat(sampleRate, channels, 16), pcm.ToArray());
  }

  private static IReadOnlyDictionary<string, string>? ReadComments(FormatCreateOptions options) {
    Dictionary<string, string>? comments = null;
    foreach (var (key, value) in options.FormatSpecific) {
      if (!key.StartsWith("tag.", StringComparison.OrdinalIgnoreCase)) continue;
      comments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      comments[key[4..]] = value;
    }
    return comments;
  }

  private static short[] ToShorts(byte[] data) {
    if ((data.Length & 1) != 0) throw new InvalidDataException("PCM16 byte count must be even.");
    var result = new short[data.Length / 2];
    for (var i = 0; i < result.Length; ++i)
      result[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2, 2));
    return result;
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.ogg", "Container", blob),
    };

    AddDecodedChannels(blob, entries);
    var parser = new OggPageParser();
    var pages = parser.Pages(blob);
    var serials = pages.Select(static page => page.Serial).Distinct().ToArray();

    foreach (var serial in serials) {
      var packets = parser.StreamPackets(blob, serial).ToArray();
      entries.Add(($"stream_{serial:X8}/packets.bin", "Stream", ConcatenateWithLengthPrefix(packets)));
      if (packets.Length < 2) continue;
      var p1 = packets[1];
      (string Tag, int Offset)? probe =
        p1.Length >= 7 && p1[0] == 0x03 && Encoding.ASCII.GetString(p1, 1, 6) == "vorbis" ? ("vorbis", 7) :
        p1.Length >= 8 && Encoding.ASCII.GetString(p1, 0, 8) == "OpusTags" ? ("opus", 8) : null;
      if (probe is null) continue;
      var parsed = new VorbisCommentReader().Read(p1.AsSpan(probe.Value.Offset));
      var commentText = new StringBuilder();
      commentText.AppendLine($"Vendor: {parsed.Vendor}");
      foreach (var (key, value) in parsed.Comments) commentText.AppendLine($"{key}={value}");
      entries.Add(($"stream_{serial:X8}/comments.txt", "Tag", Encoding.UTF8.GetBytes(commentText.ToString())));
    }
    return entries;
  }

  private static void AddDecodedChannels(byte[] blob, List<(string Name, string Kind, byte[] Data)> entries) {
    try {
      using var source = new MemoryStream(blob, writable: false);
      var descriptor = new OggFormatDescriptor();
      var pcm = descriptor.DecodePcm(source);
      if (pcm.InterleavedData.Length == 0) return;
      if (pcm.Format.Channels == 1)
        entries.Add(("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm.InterleavedData, 1, pcm.Format.SampleRate, 16)));
      else
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcm.InterleavedData, pcm.Format.Channels, pcm.Format.SampleRate, 16))
          entries.Add(($"{name}.wav", "Channel", wav));
    } catch {
      // Unsupported/multiplexed Ogg remains available through raw packet entries.
    }
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i)
      if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
    return -1;
  }

  private static byte[] ConcatenateWithLengthPrefix(byte[][] packets) {
    using var memory = new MemoryStream();
    Span<byte> length = stackalloc byte[4];
    foreach (var packet in packets) {
      BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)packet.Length));
      memory.Write(length);
      memory.Write(packet);
    }
    return memory.ToArray();
  }

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }
}
