#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.AicaAdpcm;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Aica;

/// <summary>
/// Raw Yamaha AICA ADPCM (<c>.aica</c>) sample data.
/// <para>
/// An AICA sample is headerless: the file contains only packed 4-bit codes and
/// therefore carries no signature, sample rate, channel count, loop points, or
/// exact odd-sample tail length. One raw sample belongs to one AICA sound slot,
/// so this descriptor is deliberately mono. The Dreamcast streaming convention
/// of 22050 Hz is used when decoding; a writer may accept another positive rate,
/// but that rate remains out-of-band because there is nowhere in the file to put
/// it.
/// </para>
/// <para>
/// Besides the archive view (<c>FULL.aica</c>, decoded <c>MONO.wav</c>, and
/// <c>metadata.ini</c>), the descriptor participates directly in the common
/// audio pipeline: PCM decode/encode and packet-preserving demux/mux/remux.
/// </para>
/// </summary>
public sealed class AicaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable,
  IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  /// <summary>Canonical codec identifier used by the audio conversion pipeline.</summary>
  public const string CodecId = "aica-adpcm";

  /// <summary>Assumed sample rate for headerless AICA ADPCM (Dreamcast streaming default).</summary>
  public const int AssumedSampleRate = 22050;

  private static readonly string[] AudioCodecs = [CodecId];

  public string Id => "Aica";
  public string DisplayName => "Yamaha AICA ADPCM (Dreamcast)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".aica";
  public IReadOnlyList<string> Extensions => [".aica"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Headerless: no byte signature exists for raw AICA ADPCM. Dispatch is by extension only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Headerless mono Yamaha AICA 4-bit ADPCM; PCM encode/decode and encoded-packet mux/demux are supported, with 22050 Hz assumed on decode.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable ─────────────────────────────────────────────────────

  /// <summary>
  /// Rebuilds raw AICA data from <c>FULL.aica</c> or exactly one mono integer-PCM WAV.
  /// Multiple WAVs are rejected rather than silently dropping every channel after the first.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    var full = fileList.FirstOrDefault(static f =>
      Path.GetFileName(f.Name).Equals("FULL.aica", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavs = fileList
      .Where(static f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .ToArray();
    if (wavs.Length != 1)
      throw new InvalidOperationException(
        $"AICA create needs exactly one mono WAV (or FULL.aica); got {wavs.Length} WAV inputs.");

    var parsed = new WavReader().ReadCanonicalPcm(wavs[0].Data!);
    if (parsed.NumChannels != 1)
      throw new InvalidOperationException("A raw AICA sample is mono; supply a single-channel WAV.");
    if (parsed.FormatCode != 1 || parsed.BitsPerSample is not (8 or 16 or 24 or 32))
      throw new NotSupportedException(
        $"AICA encoding accepts 8/16/24/32-bit integer PCM WAV input; got format {parsed.FormatCode}, {parsed.BitsPerSample} bits.");

    var pcm16 = parsed.BitsPerSample == 16
      ? parsed.InterleavedPcm
      : PcmCodec.Requantize(parsed.InterleavedPcm, parsed.BitsPerSample, 16);
    output.Write(AicaAdpcmCodec.Encode(ReadPcm16(pcm16)));
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "AICA accepts FULL.aica, metadata.ini, or exactly one mono 8/16/24/32-bit integer PCM WAV";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.aica" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }

    reason = $"not an AICA input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── canonical PCM route ───────────────────────────────────────────────────

  public IReadOnlyList<string> SupportedEncodeCodecs => AudioCodecs;

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var encoded = ReadAll(input);
    var pcm = ShortsToLePcm(AicaAdpcmCodec.Decode(encoded));
    return new AudioPcmBuffer(
      new AudioPcmFormat(AssumedSampleRate, 1, 16, AudioPcmEncoding.SignedInteger),
      pcm);
  }

  public bool CanEncode(
    AudioPcmFormat format,
    string codecId,
    FormatCreateOptions options,
    out string? reason
  ) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(codecId);
    ArgumentNullException.ThrowIfNull(options);

    if (!codecId.Equals(CodecId, StringComparison.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not Yamaha AICA ADPCM";
      return false;
    }
    if (format.Channels != 1) {
      reason = "raw AICA sample memory is one stream per sound slot; this .aica format has no multichannel framing";
      return false;
    }
    if (format.SampleRate <= 0) {
      reason = "AICA PCM input needs a positive sample rate (the raw file does not store it)";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "AICA encoding requires signed PCM16 input";
      return false;
    }

    reason = null;
    return true;
  }

  public void EncodePcm(
    Stream output,
    AudioPcmBuffer pcm,
    string codecId,
    FormatCreateOptions options
  ) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);

    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    output.Write(AicaAdpcmCodec.Encode(ReadPcm16(pcm.InterleavedData)));
  }

  // ── encoded packet route ──────────────────────────────────────────────────

  public IReadOnlyList<string> SupportedMuxCodecs => AudioCodecs;

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    stream = new AudioEncodedStream(
      new AudioStreamFormat(CodecId, AssumedSampleRate, 1, 4),
      [new AudioPacket(data, checked(data.LongLength * 2))]);
    return true;
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (!stream.CodecId.Equals(CodecId, StringComparison.OrdinalIgnoreCase)) {
      reason = $"codec '{stream.CodecId}' is not Yamaha AICA ADPCM";
      return false;
    }
    if (stream.Channels != 1) {
      reason = "raw .aica has no standardized multichannel framing";
      return false;
    }
    if (stream.SampleRate <= 0) {
      reason = "encoded AICA stream metadata must carry a positive sample rate even though raw .aica cannot store it";
      return false;
    }
    if (stream.BitsPerSample is not (0 or 4)) {
      reason = $"AICA is a 4-bit codec, got {stream.BitsPerSample} coded bits/sample";
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
    if (stream.CodecPrivateData is { Length: > 0 })
      throw new NotSupportedException("raw .aica has no place to preserve codec-private/header data");

    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new NotSupportedException("raw .aica has no header packets");

      var representedSamples = checked(packet.Data.LongLength * 2);
      if (packet.DurationSamples > 0 && packet.DurationSamples != representedSamples)
        throw new NotSupportedException(
          $"raw .aica cannot preserve a trimmed packet duration of {packet.DurationSamples} samples for {packet.Data.Length} bytes ({representedSamples} samples)");

      output.Write(packet.Data);
    }
  }

  // ── archive projection helpers ────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.aica", "Container", blob),
    };

    if (blob.Length > 0) {
      var le = ShortsToLePcm(AicaAdpcmCodec.Decode(blob));
      entries.Add(new(
        "MONO.wav",
        "Channel",
        PcmCodec.ToWavBlob(le, channels: 1, AssumedSampleRate, bitsPerSample: 16),
        "pcm"));
    }

    var info = new StringBuilder();
    info.AppendLine("; Raw AICA ADPCM is headerless; the following are assumed defaults.");
    info.AppendLine("codec=Yamaha AICA ADPCM (4-bit)");
    info.Append("sample_rate=").AppendLine(AssumedSampleRate.ToString(CultureInfo.InvariantCulture));
    info.AppendLine("channels=1");
    info.AppendLine("bits_per_sample=16 (decoded)");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
    return pcm;
  }

  private static short[] ReadPcm16(ReadOnlySpan<byte> pcm) {
    if ((pcm.Length & 1) != 0)
      throw new InvalidDataException("PCM16 payload has odd byte length.");

    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
    return samples;
  }
}
