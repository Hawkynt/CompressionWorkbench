#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Ircam;

/// <summary>
/// Exposes an IRCAM / BICSF (<c>.sf</c>) sound file as an archive of <c>FULL.sf</c>,
/// one mono WAV per channel (16-bit/8-bit linear PCM byte-swapped to little-endian,
/// or 32-bit IEEE-float channels wrapped as float WAVs) and a <c>metadata.ini</c>
/// carrying sample rate, channels, format and endianness. Unsupported sample formats
/// are surfaced as <c>FULL.sf</c> only — no channel entries, no throw.
/// </summary>
public sealed class IrcamFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ircam";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "IRCAM / BICSF (.sf)";
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
public string DefaultExtension => ".sf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sf", ".ircam"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x64, 0xA3, 0x01, 0x00], Confidence: 0.90),
    new([0x00, 0x01, 0xA3, 0x64], Confidence: 0.90),
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
public string Description => "IRCAM / BICSF (.sf) sound; linear PCM / float decoded to per-channel WAV.";

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

  // ── IArchiveCreatable: assemble an IRCAM file from per-channel mono WAVs ──────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.sf", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("IRCAM archive create needs either FULL.sf or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");
    if (first.BitsPerSample != 16)
      throw new InvalidOperationException("IRCAM create writes 16-bit PCM; channel WAVs must be 16-bit.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);
    var blob = new IrcamWriter().Write(interleaved, channels.Count, first.SampleRate);
    output.Write(blob);
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "IRCAM archive accepts: FULL.sf, LEFT/RIGHT/CENTER/… .wav (per-channel), metadata.ini";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.sf" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null; return true;
    }
    reason = $"not an IRCAM-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new IrcamReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sf", "Container", blob),
    };

    if (parsed.Channels >= 1)
      AddChannels(entries, parsed);

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"channels={parsed.Channels}");
    info.AppendLine($"sample_format={parsed.SampleFormat} ({FormatName(parsed.SampleFormat)})");
    info.AppendLine($"endianness={(parsed.LittleEndian ? "little" : "big")}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static void AddChannels(List<AudioPseudoArchive.Entry> entries, IrcamReader.ParsedIrcam p) {
    switch (p.SampleFormat) {
      case 1: { // 8-bit linear PCM
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
            (byte[])p.SampleData.Clone(), p.Channels, p.SampleRate, 8))
          entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
        break;
      }
      case 2: { // 16-bit linear PCM
        var le = p.LittleEndian ? (byte[])p.SampleData.Clone() : SwapSampleEndianness(p.SampleData, 2);
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(le, p.Channels, p.SampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
        break;
      }
      case 4: { // 32-bit IEEE float
        var le = p.LittleEndian ? (byte[])p.SampleData.Clone() : SwapSampleEndianness(p.SampleData, 4);
        foreach (var (name, wavBlob) in SplitFloat(le, p.Channels, p.SampleRate))
          entries.Add(new($"{name}.wav", "Channel", wavBlob, "float"));
        break;
      }
      default:
        break; // unsupported → FULL-only
    }
  }

  /// <summary>
  /// Splits interleaved little-endian 32-bit float PCM into per-channel mono float
  /// WAV blobs (RIFF format code 3). Mirrors <see cref="PcmCodec.SplitInterleavedPcm"/>'s
  /// frame walk, which always writes integer (code 1) output.
  /// </summary>
  private static IReadOnlyList<(string Name, byte[] WavBlob)> SplitFloat(byte[] interleaved, int channels, int sampleRate) {
    const int bytesPerSample = 4;
    if (channels <= 1)
      return [("MONO", PcmCodec.ToWavBlob(interleaved, 1, sampleRate, 32, formatCode: 3))];

    var frameBytes = bytesPerSample * channels;
    if (interleaved.Length % frameBytes != 0)
      throw new ArgumentException("Interleaved float PCM length is not a multiple of frame size.");

    var frameCount = interleaved.Length / frameBytes;
    var names = ChannelLayout.DefaultNames(channels);
    var result = new List<(string, byte[])>(channels);
    for (var c = 0; c < channels; ++c) {
      var mono = new byte[frameCount * bytesPerSample];
      for (var f = 0; f < frameCount; ++f)
        Buffer.BlockCopy(interleaved, f * frameBytes + c * bytesPerSample, mono, f * bytesPerSample, bytesPerSample);
      result.Add((names[c], PcmCodec.ToWavBlob(mono, 1, sampleRate, 32, formatCode: 3)));
    }
    return result;
  }

  private static byte[] SwapSampleEndianness(byte[] pcm, int bytesPerSample) {
    if (bytesPerSample <= 1) return (byte[])pcm.Clone();
    var swapped = new byte[pcm.Length];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }

  private static string FormatName(uint format) => format switch {
    1 => "8-bit linear PCM",
    2 => "16-bit linear PCM",
    4 => "32-bit IEEE float",
    _ => $"unsupported ({format})",
  };
}
