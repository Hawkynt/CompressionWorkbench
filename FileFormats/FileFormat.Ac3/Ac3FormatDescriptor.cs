#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Ac3;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Ac3;

/// <summary>
/// AC-3 / E-AC-3 (Dolby Digital / Dolby Digital Plus) elementary streams. Besides the
/// pseudo-archive view, the descriptor can encode legacy AC-3 from canonical PCM, create an
/// elementary stream from extracted mono WAV channels, and expose complete syncframes as encoded
/// packets for byte-preserving demux/mux/remux. E-AC-3 remains decode/remux-only: it is preserved
/// as E-AC-3 packets rather than being silently re-encoded as legacy AC-3.
/// </summary>
public sealed class Ac3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly string[] EncodeCodecs = ["ac3"];
  private static readonly string[] MuxCodecs = ["ac3", "eac3"];
  private static readonly int[] LegacyBitrates = [
    32_000, 40_000, 48_000, 56_000, 64_000, 80_000, 96_000, 112_000, 128_000, 160_000,
    192_000, 224_000, 256_000, 320_000, 384_000, 448_000, 512_000, 576_000, 640_000,
  ];

  /// <summary>Gets the id.</summary>
  public string Id => "Ac3";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "AC-3 / E-AC-3";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".ac3";
  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".ac3", ".eac3"];
  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Ac3SyncFrame.SyncWord, Confidence: 0.60),
  ];
  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ac3", "AC-3")];
  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Gets the description.</summary>
  public string Description =>
    "AC-3 / E-AC-3 elementary audio; legacy AC-3 encode plus syncframe-preserving AC-3/E-AC-3 demux and remux.";

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>Decodes the supplied input.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>Performs the extract entry operation.</summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── Creation from the pseudo-archive surface ──────────────────────────────

  /// <summary>Gets the max total archive size.</summary>
  public long? MaxTotalArchiveSize => null;

  /// <summary>Gets the accepted inputs description.</summary>
  public string AcceptedInputsDescription =>
    "AC-3 accepts FULL.ac3/FULL.eac3 for byte-exact pass-through, or PCM16 mono WAV channel files; metadata.ini is ignored.";

  /// <summary>Reports whether an input can participate in AC-3 creation.</summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.ac3", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FULL.eac3", StringComparison.OrdinalIgnoreCase)
        || name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }

    reason = $"not an AC-3 pseudo-archive input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  /// <summary>
  /// Writes a byte-exact supplied FULL stream, or interleaves per-channel PCM16 WAVs and encodes
  /// legacy AC-3. Ambiguous channel counts use the conventional default layout; every other legacy
  /// layout can be selected explicitly with <c>acmod</c> and <c>lfe</c> format options.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file => {
      var name = Path.GetFileName(file.Name);
      return name.Equals("FULL.ac3", StringComparison.OrdinalIgnoreCase)
             || name.Equals("FULL.eac3", StringComparison.OrdinalIgnoreCase);
    });
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var wavs = files
      .Where(static file => Path.GetFileName(file.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .ToArray();
    if (wavs.Length == 0)
      throw new InvalidOperationException("AC-3 creation needs FULL.ac3/FULL.eac3 or one or more mono WAV channel files.");

    var channels = wavs.Select(static file => new WavReader().ReadCanonicalPcm(file.Data)).ToArray();
    var first = channels[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample != 16)
      throw new InvalidOperationException("AC-3 creation requires PCM16 mono WAV channel inputs.");
    if (channels.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1
                                || channel.BitsPerSample != 16 || channel.SampleRate != first.SampleRate
                                || channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All AC-3 channel WAVs must be PCM16 mono with matching sample rate and frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), 16);
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(first.SampleRate, channels.Length, 16), interleaved);
    var codec = options.Method ?? options.GetString("codec") ?? "ac3";
    this.EncodePcm(output, pcm, codec, options);
  }

  // ── Canonical PCM encode/decode ───────────────────────────────────────────

  /// <summary>Gets the codecs that can be encoded from PCM.</summary>
  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;

  /// <summary>Reports whether the canonical PCM geometry and requested AC-3 options are encodable.</summary>
  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);

    if (!codecId.Equals("ac3", StringComparison.OrdinalIgnoreCase)) {
      reason = codecId.Equals("eac3", StringComparison.OrdinalIgnoreCase)
        ? "E-AC-3 encoding is not implemented; E-AC-3 is supported losslessly through demux/mux/remux"
        : $"codec '{codecId}' is not AC-3";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "AC-3 encoding requires signed PCM16 input";
      return false;
    }
    if (format.SampleRate is not (32_000 or 44_100 or 48_000)) {
      reason = "legacy AC-3 encoding supports 32, 44.1, or 48 kHz";
      return false;
    }
    if (format.Channels is < 1 or > 6) {
      reason = "legacy AC-3 supports 1 to 6 coded channels";
      return false;
    }
    if (!TryResolveLayout(format.Channels, options, out _, out _, out reason))
      return false;

    var bitrate = NormalizeBitrate(options.GetOptionInt("bitrate", DefaultBitrate(format.Channels)));
    if (Array.IndexOf(LegacyBitrates, bitrate) < 0) {
      reason = "AC-3 bitrate must be one of 32,40,48,56,64,80,96,112,128,160,192,224,256,320,384,448,512,576,640 kbit/s";
      return false;
    }

    var dialNorm = options.GetOptionInt("dialnorm", -31);
    if (dialNorm is < -31 or > -1) {
      reason = "dialnorm must be -31..-1 dB";
      return false;
    }

    var cutoff = options.GetOptionInt("cutoff", 0);
    if (cutoff < 0 || cutoff > format.SampleRate / 2) {
      reason = "cutoff must be zero (automatic) or within the Nyquist limit";
      return false;
    }

    reason = null;
    return true;
  }

  /// <summary>Encodes interleaved canonical PCM16 to a legacy AC-3 elementary stream.</summary>
  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    _ = TryResolveLayout(pcm.Format.Channels, options, out var acmod, out var lfe, out _);
    if ((pcm.InterleavedData.Length & 1) != 0)
      throw new InvalidDataException("PCM16 payload has odd length.");

    var samples = new short[pcm.InterleavedData.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.InterleavedData.AsSpan(i * 2, 2));

    var bitrate = NormalizeBitrate(options.GetOptionInt("bitrate", DefaultBitrate(pcm.Format.Channels)));
    var encoded = Ac3Codec.Encode(samples, new Ac3EncoderOptions(
      pcm.Format.SampleRate,
      bitrate,
      acmod,
      lfe,
      options.GetOptionInt("dialnorm", -31),
      options.GetOptionInt("cutoff", 0),
      PadFinalFrame: options.GetOptionBool("pad-final-frame", true)));
    output.Write(encoded);
  }

  /// <summary>Decodes the primary AC-3/E-AC-3 programme to canonical interleaved PCM16.</summary>
  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var blob = ReadAll(input);
    using var probe = new MemoryStream(blob, writable: false);
    var info = Ac3Codec.ReadStreamInfo(probe);
    using var source = new MemoryStream(blob, writable: false);
    using var pcm = new MemoryStream();
    Ac3Codec.Decompress(source, pcm);
    if (pcm.Length == 0 && info.DurationSamples > 0)
      throw new NotSupportedException("The AC-3 decoder produced no PCM from a stream that declares audio samples.");
    return new AudioPcmBuffer(
      new AudioPcmFormat(info.SampleRate, info.Channels, 16, AudioPcmEncoding.SignedInteger),
      pcm.ToArray());
  }

  // ── Syncframe packet demux/mux ────────────────────────────────────────────

  /// <summary>Gets the elementary codecs this raw syncframe stream can contain.</summary>
  public IReadOnlyList<string> SupportedMuxCodecs => MuxCodecs;

  /// <summary>
  /// Reports whether this raw elementary-stream target can carry the encoded stream. AC-3
  /// syncframes are already codec access units, so muxing adds no wrapper bytes.
  /// </summary>
  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    var enhanced = stream.CodecId.Equals("eac3", StringComparison.OrdinalIgnoreCase);
    if (!enhanced && !stream.CodecId.Equals("ac3", StringComparison.OrdinalIgnoreCase)) {
      reason = $"AC-3 elementary streams cannot carry codec '{stream.CodecId}'";
      return false;
    }

    var legalRate = enhanced
      ? stream.SampleRate is 16_000 or 22_050 or 24_000 or 32_000 or 44_100 or 48_000
      : stream.SampleRate is 32_000 or 44_100 or 48_000;
    if (!legalRate) {
      reason = enhanced
        ? "E-AC-3 syncframes use 16, 22.05, 24, 32, 44.1, or 48 kHz"
        : "AC-3 syncframes use 32, 44.1, or 48 kHz";
      return false;
    }

    if (stream.Channels is < 1 or > 6) {
      reason = "one AC-3/E-AC-3 independent substream describes 1 to 6 channels";
      return false;
    }

    reason = null;
    return true;
  }

  /// <summary>Concatenates validated complete AC-3/E-AC-3 syncframe packets byte for byte.</summary>
  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);

    var expectEnhanced = stream.Format.CodecId.Equals("eac3", StringComparison.OrdinalIgnoreCase);
    var written = 0;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        continue;
      if (packet.Data.Length == 0)
        throw new InvalidDataException("AC-3 muxing cannot carry an empty syncframe.");
      if (Ac3FrameHeader.TryParse(packet.Data, 0) is not { } header || header.FrameSize != packet.Data.Length)
        throw new InvalidDataException("AC-3 mux packet is not exactly one complete syncframe.");
      if (header.IsEnhanced != expectEnhanced)
        throw new InvalidDataException($"{stream.Format.CodecId} stream contains a {(header.IsEnhanced ? "E-AC-3" : "AC-3")} frame.");
      if (header.SampleRate != stream.Format.SampleRate)
        throw new InvalidDataException($"syncframe sample rate {header.SampleRate} does not match stream rate {stream.Format.SampleRate}.");

      output.Write(packet.Data);
      ++written;
    }

    if (written == 0)
      throw new ArgumentException("AC-3 muxing requires at least one syncframe packet.", nameof(stream));
  }

  /// <summary>
  /// Splits a raw AC-3/E-AC-3 byte stream into complete syncframes without changing a byte. E-AC-3
  /// dependent substreams are retained as packets; they are not discarded merely because the PCM
  /// decoder consumes only the primary programme.
  /// </summary>
  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var blob = ReadAll(input);
    if (Ac3FrameHeader.TryParse(blob, 0) is not { } first || first.FrameSize <= 0 || first.FrameSize > blob.Length) {
      stream = null;
      return false;
    }

    var packets = new List<AudioPacket>();
    var offset = 0;
    while (offset < blob.Length) {
      if (Ac3FrameHeader.TryParse(blob, offset) is not { } header
          || header.FrameSize <= 0 || offset + header.FrameSize > blob.Length
          || header.IsEnhanced != first.IsEnhanced
          || header.SampleRate != first.SampleRate) {
        stream = null;
        return false;
      }

      if (!first.IsEnhanced && (header.Acmod != first.Acmod
                                || header.LowFrequencyEffects != first.LowFrequencyEffects)) {
        stream = null;
        return false;
      }

      if (first.IsEnhanced && header.IsIndependentSubstream && header.SubstreamId == 0
          && (header.Acmod != first.Acmod || header.LowFrequencyEffects != first.LowFrequencyEffects)) {
        stream = null;
        return false;
      }

      packets.Add(new AudioPacket(
        blob.AsSpan(offset, header.FrameSize).ToArray(),
        DurationSamples: header.IsEnhanced ? header.NumBlocks * 256L : 1536L));
      offset += header.FrameSize;
    }

    if (packets.Count == 0) {
      stream = null;
      return false;
    }

    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["bsid"] = first.Bsid.ToString(CultureInfo.InvariantCulture),
      ["acmod"] = first.Acmod.ToString(CultureInfo.InvariantCulture),
      ["lfe"] = first.LowFrequencyEffects ? "1" : "0",
    };
    if (first.IsEnhanced) {
      properties["stream-type"] = first.StreamType.ToString(CultureInfo.InvariantCulture);
      properties["substream-id"] = first.SubstreamId.ToString(CultureInfo.InvariantCulture);
    }

    stream = new AudioEncodedStream(
      new AudioStreamFormat(
        first.IsEnhanced ? "eac3" : "ac3",
        first.SampleRate,
        Ac3FrameHeader.AcmodChannelCount(first.Acmod) + (first.LowFrequencyEffects ? 1 : 0),
        Properties: properties),
      packets);
    return true;
  }

  // ── Shared pseudo-archive entry builder ───────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var firstOffset = IndexOfSync(blob, 0);
    var isEnhanced = firstOffset >= 0 && Ac3FrameHeader.TryParse(blob, firstOffset) is { IsEnhanced: true };
    var entries = new List<AudioPseudoArchive.Entry> {
      new(isEnhanced ? "FULL.eac3" : "FULL.ac3", "Container", blob, isEnhanced ? "eac3" : "ac3"),
    };
    AddDecodedChannels(blob, entries);
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadata(blob))));
    return entries;
  }

  /// <summary>
  /// Decodes the elementary stream and adds one mono WAV per decoded channel. Malformed or
  /// unsupported streams degrade to FULL + metadata instead of making the raw stream inaccessible.
  /// </summary>
  private static void AddDecodedChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    try {
      Ac3StreamInfo info;
      using (var infoStream = new MemoryStream(blob, writable: false))
        info = Ac3Codec.ReadStreamInfo(infoStream);
      if (info.Channels < 1 || info.SampleRate <= 0)
        return;

      byte[] pcm;
      using (var src = new MemoryStream(blob, writable: false))
      using (var dst = new MemoryStream()) {
        Ac3Codec.Decompress(src, dst);
        pcm = dst.ToArray();
      }
      if (pcm.Length == 0)
        return;

      var names = AcmodChannelNames(info.Acmod, info.Lfe);
      if (names.Count != info.Channels)
        return;

      var bytesPerFrame = info.Channels * 2;
      if (pcm.Length % bytesPerFrame != 0)
        return;
      var frameCount = pcm.Length / bytesPerFrame;

      if (info.Channels == 1) {
        entries.Add(new($"{names[0]}.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, info.SampleRate, 16), "pcm"));
        return;
      }

      for (var channel = 0; channel < info.Channels; ++channel) {
        var mono = new byte[frameCount * 2];
        for (var frame = 0; frame < frameCount; ++frame) {
          var source = (frame * info.Channels + channel) * 2;
          mono[frame * 2] = pcm[source];
          mono[frame * 2 + 1] = pcm[source + 1];
        }
        entries.Add(new($"{names[channel]}.wav", "Channel", mono.Length == 0
          ? PcmCodec.ToWavBlob([], 1, info.SampleRate, 16)
          : PcmCodec.ToWavBlob(mono, 1, info.SampleRate, 16), "pcm"));
      }
    } catch {
      // Undecodable/unsupported/truncated input: FULL + metadata remain available.
    }
  }

  /// <summary>
  /// AC-3 acmod to the decoder's canonical WAVE/ITU interleave order. LFE sits at the WAVE LFE
  /// position, before back/side channels, rather than being blindly appended after them.
  /// </summary>
  private static IReadOnlyList<string> AcmodChannelNames(int acmod, bool lfe) => (acmod, lfe) switch {
    (0, false) => ["CH_0", "CH_1"],
    (0, true) => ["CH_0", "CH_1", "LFE"],
    (1, false) => ["CENTER"],
    (1, true) => ["CENTER", "LFE"],
    (2, false) => ["LEFT", "RIGHT"],
    (2, true) => ["LEFT", "RIGHT", "LFE"],
    (3, false) => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER"],
    (3, true) => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE"],
    (4, false) => ["FRONT_LEFT", "FRONT_RIGHT", "BACK_CENTER"],
    (4, true) => ["FRONT_LEFT", "FRONT_RIGHT", "LFE", "BACK_CENTER"],
    (5, false) => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "BACK_CENTER"],
    (5, true) => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "BACK_CENTER"],
    (6, false) => ["FRONT_LEFT", "FRONT_RIGHT", "SIDE_LEFT", "SIDE_RIGHT"],
    (6, true) => ["FRONT_LEFT", "FRONT_RIGHT", "LFE", "SIDE_LEFT", "SIDE_RIGHT"],
    (7, false) => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "SIDE_LEFT", "SIDE_RIGHT"],
    (7, true) => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "SIDE_LEFT", "SIDE_RIGHT"],
    _ => [],
  };

  private static string BuildMetadata(byte[] blob) {
    var info = new StringBuilder();

    var firstOffset = IndexOfSync(blob, 0);
    if (firstOffset < 0 || Ac3SyncFrame.TryParse(blob, firstOffset) is not { } first) {
      info.AppendLine("codec=AC-3");
      info.AppendLine("frames=0");
      info.AppendLine("note=no parseable AC-3 sync frame found.");
      return info.ToString();
    }

    info.AppendLine($"codec={(first.IsEnhanced ? "E-AC-3 (Dolby Digital Plus)" : "AC-3 (Dolby Digital)")}");
    var channels = Ac3SyncFrame.AcmodChannelCount(first.Acmod) + (first.LowFrequencyEffects ? 1 : 0);
    info.AppendLine($"channel_layout={Ac3SyncFrame.LayoutName(first.Acmod, first.LowFrequencyEffects)}");
    info.AppendLine($"channels={channels}");
    info.AppendLine($"acmod={first.Acmod}");
    info.AppendLine($"lfe={(first.LowFrequencyEffects ? "yes" : "no")}");
    info.AppendLine($"sample_rate={first.SampleRate}");
    info.AppendLine($"bitrate={first.Bitrate}");
    info.AppendLine($"bsid={first.Bsid}");
    info.AppendLine($"dialnorm=-{first.DialNorm} dBFS");

    var frames = 0;
    var dependentFrames = 0;
    long totalSamples = 0;
    var pos = firstOffset;
    while (pos + 6 <= blob.Length) {
      if (Ac3FrameHeader.TryParse(blob, pos) is not { } header || header.FrameSize <= 0)
        break;
      ++frames;
      if (header.IsDependentSubstream || (header.IsEnhanced && header.SubstreamId != 0))
        ++dependentFrames;
      else
        totalSamples += header.IsEnhanced ? header.NumBlocks * 256L : 1536L;
      pos += header.FrameSize;
    }

    var duration = first.SampleRate > 0 ? (double)totalSamples / first.SampleRate : 0;
    info.AppendLine($"frames={frames}");
    info.AppendLine($"duration_seconds={duration:0.###}");
    if (dependentFrames > 0)
      info.AppendLine($"note=skipped {dependentFrames} dependent/non-primary E-AC-3 substream frame(s) (only independent substream 0 is decoded)." );
    return info.ToString();
  }

  private static bool TryResolveLayout(int channels, FormatCreateOptions options,
    out int acmod, out bool lfe, out string? reason) {
    if (options.TryGetInt("acmod", out var requestedAcmod)) {
      acmod = requestedAcmod;
      lfe = options.GetOptionBool("lfe", false);
      if (acmod is < 1 or > 7) {
        reason = acmod == 0
          ? "dual-mono acmod=0 remains decode/remux-only; the managed encoder currently emits acmod 1..7"
          : "legacy AC-3 acmod must be 1..7 for encoding";
        return false;
      }
      var expected = Ac3FrameHeader.AcmodChannelCount(acmod) + (lfe ? 1 : 0);
      if (expected != channels) {
        reason = $"acmod={acmod}, lfe={lfe} expects {expected} channels, input has {channels}";
        return false;
      }
      reason = null;
      return true;
    }

    (acmod, lfe) = channels switch {
      1 => (1, false),
      2 => (2, false),
      3 => (3, false),
      4 => (6, false),
      5 => (7, false),
      6 => (7, true),
      _ => (0, false),
    };
    reason = acmod == 0 ? $"no default legacy AC-3 layout for {channels} channels" : null;
    return acmod != 0;
  }

  private static int DefaultBitrate(int channels) => channels switch {
    1 => 96_000,
    2 => 192_000,
    3 => 256_000,
    4 => 384_000,
    _ => 448_000,
  };

  private static int NormalizeBitrate(int bitrate) => bitrate is > 0 and < 1_000 ? bitrate * 1_000 : bitrate;

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek)
      input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }

  private static int IndexOfSync(byte[] blob, int start) {
    for (var i = start; i + 1 < blob.Length; ++i)
      if (blob[i] == 0x0B && blob[i + 1] == 0x77)
        return i;
    return -1;
  }
}
