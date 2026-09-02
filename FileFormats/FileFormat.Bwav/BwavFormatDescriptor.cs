#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Bwav;

/// <summary>
/// Exposes a Nintendo Switch <c>.bwav</c> stream as a pseudo-archive: <c>FULL.bwav</c> (the
/// byte-exact file), one decoded mono WAV per channel (named per <see cref="ChannelLayout"/>) and a
/// <c>metadata.ini</c> describing codec, sample rate, channels and loop. DSP-ADPCM (codec 1) and
/// PCM16 (codec 0) decode; anything else falls back to <c>FULL.bwav</c> only.
/// <para>
/// Creatable from per-channel mono WAVs (DSP-encoded into a valid BWAV that round-trips this
/// reader; the header CRC is written as <c>0</c>) or a <c>FULL.bwav</c> passthrough.
/// </para>
/// </summary>
public sealed class BwavFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Bwav";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "BWAV (Switch stream)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".bwav";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".bwav"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("BWAV"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "BWAV (Switch stream); full file + per-channel decoded WAVs + metadata.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: FULL passthrough OR per-channel mono WAVs → DSP-ADPCM BWAV ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.bwav", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("BWAV archive create needs either FULL.bwav or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().ReadCanonicalPcm(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.NumChannels != 1 || c.SampleRate != first.SampleRate || c.BitsPerSample != 16))
      throw new InvalidOperationException("All channel WAVs must be mono 16-bit and share the same sample rate.");

    var pcmChannels = channels.Select(ToShorts).ToList();
    var sampleCount = pcmChannels[0].Length;
    if (pcmChannels.Any(c => c.Length != sampleCount))
      throw new InvalidOperationException("All channel WAVs must have the same sample count.");

    output.Write(new BwavWriter().Write(pcmChannels, first.SampleRate));
  }

  private static short[] ToShorts(WavReader.ParsedWav wav) {
    var pcm = wav.InterleavedPcm;
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "BWAV archive accepts: FULL.bwav, or LEFT/RIGHT/CENTER/… .wav (per-channel mono 16-bit)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/') ?? "";
    if (dir == "" && (name == "full.bwav" || name.EndsWith(".wav"))) { reason = null; return true; }
    reason = $"not a BWAV-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.bwav", "Container", blob),
    };

    try {
      var parsed = new BwavReader().Read(blob);
      var names = ChannelLayout.DefaultNames(parsed.ChannelCount);
      for (var c = 0; c < parsed.ChannelCount; ++c) {
        var pcm = ShortsToLe(parsed.Pcm[c]);
        var wav = PcmCodec.ToWavBlob(pcm, channels: 1, parsed.Channels[c].SampleRate, bitsPerSample: 16);
        entries.Add(new($"{names[c]}.wav", "Channel", wav, "pcm"));
      }
      entries.Add(new("metadata.ini", "Tag", BuildMetadata(parsed)));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      // Unparseable / unsupported BWAV: surface FULL only.
    }

    return entries;
  }

  private static byte[] BuildMetadata(BwavReader.ParsedBwav parsed) {
    var first = parsed.Channels[0];
    var codecName = first.Codec switch {
      0 => "PCM16",
      1 => "DSP-ADPCM",
      _ => $"unknown({first.Codec})",
    };
    var sb = new StringBuilder();
    sb.Append("[bwav]\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={parsed.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"crc={parsed.Crc}\n");
    sb.Append(CultureInfo.InvariantCulture, $"channels={parsed.ChannelCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"codec={codecName}\n");
    sb.Append(CultureInfo.InvariantCulture, $"sampleRate={first.SampleRate}\n");
    sb.Append(CultureInfo.InvariantCulture, $"samples={first.SampleCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loop={(first.IsLooping ? 1 : 0)}\n");
    if (parsed.Crc == 0)
      sb.Append("note=crc field is zero (informational; not validated on playback)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ShortsToLe(short[] samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
