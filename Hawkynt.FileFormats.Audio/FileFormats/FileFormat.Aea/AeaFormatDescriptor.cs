#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Atrac1;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Aea;

/// <summary>
/// Sony MD STUDIO / MiniDisc AEA container for ATRAC1. The read path accepts the one-to-eight
/// channel layout handled by current ATRAC1 demuxers, while creation follows the interoperable
/// AEA muxing profile: ATRAC1 only, 44100 Hz, mono or stereo, 212 bytes per channel per 512 samples.
/// </summary>
public sealed class AeaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private const int HeaderSize = 2048;
  private const int SoundUnitSize = Atrac1Codec.SoundUnitSize;
  private const int BlockCountOffset = 260;
  private const int ChannelOffset = 264;
  private const uint Marker = 0x0000_0800;
  private const int SampleRate = 44100;
  private static readonly string[] Atrac1CodecIds = ["atrac1"];
  private static readonly int[] LegalBfuCounts = [20, 28, 32, 36, 40, 44, 48, 52];

  public string Id => "Aea";
  public string DisplayName => "Sony ATRAC1 / MiniDisc (.aea)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".aea";
  public IReadOnlyList<string> Extensions => [".aea"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x08, 0x00, 0x00], Confidence: 0.25),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("atrac1", "Sony ATRAC1")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Sony ATRAC1 / MiniDisc AEA; 1-8-channel decode/demux and interoperable 44.1 kHz mono/stereo encode/mux.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  public long? MaxTotalArchiveSize => null;

  public string AcceptedInputsDescription =>
    "AEA accepts FULL.aea or one/two mono 44100 Hz PCM16 WAV channel files with matching frame counts.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.aea", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }

    reason = $"not an AEA input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.aea", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var channels = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .Select(static file => new WavReader().ReadCanonicalPcm(file.Data))
      .ToArray();
    if (channels.Length is < 1 or > 2)
      throw new InvalidOperationException("AEA creation requires one or two mono WAV channels.");

    var first = channels[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample != 16 || first.SampleRate != SampleRate)
      throw new InvalidOperationException("AEA creation requires mono 44100 Hz PCM16 WAV inputs.");
    if (channels.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1
                                || channel.BitsPerSample != 16 || channel.SampleRate != SampleRate
                                || channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All AEA channel WAVs must be mono 44100 Hz PCM16 with matching frame counts.");

    var interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), 16);
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(SampleRate, channels.Length, 16), interleaved);
    var codec = options.Method ?? options.GetString("codec") ?? "atrac1";
    this.EncodePcm(output, pcm, codec, options);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => Atrac1CodecIds;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);

    if (!Atrac1CodecIds.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not ATRAC1";
      return false;
    }
    if (format.SampleRate != SampleRate) {
      reason = "AEA/ATRAC1 encoding requires 44100 Hz PCM";
      return false;
    }
    if (format.Channels is not (1 or 2)) {
      reason = "AEA/ATRAC1 encoding supports mono or stereo";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "ATRAC1 encoder input must be signed PCM16";
      return false;
    }
    if (options.HasOption("window-mask")
        && (!options.TryGetInt("window-mask", out var windowMask) || windowMask is < 0 or > 7)) {
      reason = "ATRAC1 window-mask must be an integer from 0 through 7";
      return false;
    }
    if (options.HasOption("bfu-count")
        && (!options.TryGetInt("bfu-count", out var bfuCount) || Array.IndexOf(LegalBfuCounts, bfuCount) < 0)) {
      reason = "ATRAC1 bfu-count must be one of 20, 28, 32, 36, 40, 44, 48 or 52";
      return false;
    }

    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    if (pcm.InterleavedData.Length % pcm.Format.BytesPerFrame != 0)
      throw new InvalidDataException("PCM payload is not a whole number of interleaved sample frames.");

    var samples = new short[pcm.InterleavedData.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.InterleavedData.AsSpan(i * 2, 2));

    var encoderOptions = new Atrac1EncoderOptions {
      WindowMask = options.GetOptionInt("window-mask", 0),
      BfuCount = options.GetOptionInt("bfu-count", 52),
    };
    var payload = new Atrac1Encoder(pcm.Format.Channels, encoderOptions).EncodeStream(samples);
    WriteContainer(output, pcm.Format.Channels, options.GetString("title") ?? string.Empty, payload);
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    if (!TryReadHeader(data, out var header))
      throw new InvalidDataException("Invalid or truncated AEA container.");

    var payload = data.AsSpan(HeaderSize);
    var samples = new Atrac1Codec(header.Channels).DecodeStream(payload);
    var pcm = new byte[checked(samples.Length * 2)];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
    return new AudioPcmBuffer(new AudioPcmFormat(SampleRate, header.Channels, 16), pcm);
  }

  public IReadOnlyList<string> SupportedMuxCodecs => Atrac1CodecIds;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!Atrac1CodecIds.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"AEA carries ATRAC1 sound units, not codec '{stream.CodecId}'";
      return false;
    }
    if (stream.SampleRate != SampleRate) {
      reason = "AEA muxing requires ATRAC1 at 44100 Hz";
      return false;
    }
    if (stream.Channels is not (1 or 2)) {
      reason = "interoperable AEA muxing supports mono or stereo";
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

    var frameSize = checked(stream.Format.Channels * SoundUnitSize);
    long frameCount = 0;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        continue;
      if (packet.Data.Length % frameSize != 0)
        throw new InvalidDataException($"AEA ATRAC1 packet size {packet.Data.Length} is not a multiple of {frameSize} bytes.");
      frameCount = checked(frameCount + packet.Data.Length / frameSize);
    }

    var soundUnits = checked(frameCount * stream.Format.Channels);
    if (soundUnits > uint.MaxValue)
      throw new InvalidDataException("AEA block count exceeds the 32-bit header field.");

    var title = options.GetString("title") ?? Property(stream.Format, "title") ?? string.Empty;
    WriteHeader(output, stream.Format.Channels, title, (uint)soundUnits);
    foreach (var packet in stream.Packets)
      if (!packet.IsHeader)
        output.Write(packet.Data);
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    if (!TryReadHeader(data, out var header)) {
      stream = null;
      return false;
    }

    var frameSize = checked(header.Channels * SoundUnitSize);
    var payload = data.AsSpan(HeaderSize);
    var packets = new List<AudioPacket>(frameSize == 0 ? 0 : payload.Length / frameSize);
    for (var offset = 0; offset < payload.Length; offset += frameSize)
      packets.Add(new AudioPacket(payload.Slice(offset, frameSize).ToArray(), Atrac1Codec.SamplesPerFrame));

    stream = new AudioEncodedStream(
      new AudioStreamFormat(
        "atrac1",
        SampleRate,
        header.Channels,
        Properties: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          ["title"] = header.Title,
          ["aea-declared-block-count"] = header.DeclaredBlockCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }),
      packets);
    return true;
  }

  /// <summary>
  /// Structural AEA validation: marker, interoperable mono/stereo channel count and a payload
  /// containing at least one complete 212-byte-per-channel ATRAC1 frame. The declared block count
  /// is advisory because older writers commonly leave it zero.
  /// </summary>
  public static bool LooksLikeAea(ReadOnlySpan<byte> data) {
    if (!TryReadHeader(data, out var header) || header.Channels is not (1 or 2))
      return false;
    var frameSize = header.Channels * SoundUnitSize;
    return data.Length - HeaderSize >= frameSize;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.aea", "Container", blob),
    };

    if (!TryReadHeader(blob, out var header))
      return entries;

    var frameSize = header.Channels * SoundUnitSize;
    var actualFrames = (blob.Length - HeaderSize) / frameSize;
    var info = new StringBuilder();
    info.AppendLine("[ATRAC1]");
    info.AppendLine($"title = {header.Title}");
    info.AppendLine($"channels = {header.Channels}");
    info.AppendLine($"sample_rate = {SampleRate}");
    info.AppendLine($"sound_unit_bytes = {SoundUnitSize}");
    info.AppendLine($"declared_block_count = {header.DeclaredBlockCount}");
    info.AppendLine($"frames = {actualFrames}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    if (actualFrames == 0)
      return entries;

    try {
      var interleaved = new Atrac1Codec(header.Channels).DecodeStream(blob.AsSpan(HeaderSize));
      if (interleaved.Length == 0)
        return entries;

      var le = new byte[interleaved.Length * 2];
      for (var i = 0; i < interleaved.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2, 2), interleaved[i]);

      if (header.Channels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(le, 1, SampleRate, 16, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(le, header.Channels, SampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }
    } catch {
      // A structurally valid container may still carry malformed ATRAC1. Keep the raw/tag views.
    }

    return entries;
  }

  private static void WriteContainer(Stream output, int channels, string title, ReadOnlySpan<byte> payload) {
    var frameSize = checked(channels * SoundUnitSize);
    if (payload.Length % frameSize != 0)
      throw new InvalidDataException("ATRAC1 payload is not a whole number of AEA frames.");
    var frameCount = payload.Length / frameSize;
    var soundUnits = checked((long)frameCount * channels);
    if (soundUnits > uint.MaxValue)
      throw new InvalidDataException("AEA block count exceeds the 32-bit header field.");
    WriteHeader(output, channels, title, (uint)soundUnits);
    output.Write(payload);
  }

  private static void WriteHeader(Stream output, int channels, string title, uint blockCount) {
    var header = new byte[HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, Marker);
    var titleBytes = Encoding.Latin1.GetBytes(title);
    titleBytes.AsSpan(0, Math.Min(256, titleBytes.Length)).CopyTo(header.AsSpan(4, 256));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(BlockCountOffset, 4), blockCount);
    header[ChannelOffset] = checked((byte)channels);
    output.Write(header);
  }

  private static bool TryReadHeader(ReadOnlySpan<byte> data, out AeaHeader header) {
    header = default;
    if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Marker)
      return false;

    var channels = data[ChannelOffset];
    if (channels is < 1 or > 8)
      return false;
    var frameSize = channels * SoundUnitSize;
    if ((data.Length - HeaderSize) % frameSize != 0)
      return false;

    var declaredBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(BlockCountOffset, 4));
    header = new AeaHeader(channels, declaredBlockCount, ReadTitle(data));
    return true;
  }

  private static string ReadTitle(ReadOnlySpan<byte> data) {
    var title = data.Slice(4, 256);
    var terminator = title.IndexOf((byte)0);
    if (terminator >= 0)
      title = title[..terminator];
    return Encoding.Latin1.GetString(title);
  }

  private static string? Property(AudioStreamFormat format, string key)
    => format.Properties is { } properties && properties.TryGetValue(key, out var value) ? value : null;

  private static byte[] ReadAll(Stream stream) {
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
  }

  private readonly record struct AeaHeader(int Channels, uint DeclaredBlockCount, string Title);
}
