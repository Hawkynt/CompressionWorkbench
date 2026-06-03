#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Ast;

/// <summary>
/// Exposes a GameCube/Wii <c>.ast</c> (STRM stream) as an archive of <c>FULL.ast</c> plus, for the
/// PCM16BE coding, one decoded mono WAV per channel (named per <see cref="ChannelLayout"/>) plus a
/// <c>metadata.ini</c>. The AFC-ADPCM coding (codec 0) is not decoded: the archive then surfaces
/// <c>FULL.ast</c> plus a <c>metadata.ini</c> noting the undecoded codec. Unparseable input falls
/// back gracefully to <c>FULL.ast</c> only.
/// </summary>
public sealed class AstFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Ast";
  public string DisplayName => "AST (GameCube/Wii stream)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ast";
  public IReadOnlyList<string> Extensions => [".ast"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("STRM"u8.ToArray(), Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AST (GameCube/Wii STRM stream); full file + per-channel decoded WAVs (PCM16) + metadata.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: FULL passthrough OR per-channel mono WAVs → PCM16BE AST ──

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.ast", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("AST archive create needs either FULL.ast or one or more per-channel WAVs.");

    var channels = new List<WavReader.ParsedWav>();
    foreach (var (_, data) in channelBlobs) channels.Add(new WavReader().Read(data));

    var first = channels[0];
    if (channels.Any(c => c.NumChannels != 1 || c.SampleRate != first.SampleRate || c.BitsPerSample != 16))
      throw new InvalidOperationException("All channel WAVs must be mono 16-bit and share the same sample rate.");

    var pcmChannels = channels.Select(ToShorts).ToList();
    var sampleCount = pcmChannels[0].Length;
    if (pcmChannels.Any(c => c.Length != sampleCount))
      throw new InvalidOperationException("All channel WAVs must have the same sample count.");

    output.Write(new AstWriter().Write(pcmChannels, first.SampleRate));
  }

  private static short[] ToShorts(WavReader.ParsedWav wav) {
    var pcm = wav.InterleavedPcm;
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "AST archive accepts: FULL.ast, or LEFT/RIGHT/CENTER/… .wav (per-channel mono 16-bit)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";
    if (dir == "" && (name == "full.ast" || name.EndsWith(".wav"))) { reason = null; return true; }
    reason = $"not an AST-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.ast", "Container", blob),
    };

    try {
      var parsed = new AstReader().Read(blob);
      if (parsed.Pcm.Length > 0) {
        var names = ChannelLayout.DefaultNames(parsed.Info.NumChannels);
        for (var c = 0; c < parsed.Info.NumChannels; ++c) {
          var pcm = ShortsToLe(parsed.Pcm[c]);
          var wav = PcmCodec.ToWavBlob(pcm, channels: 1, parsed.Info.SampleRate, bitsPerSample: 16);
          entries.Add(new($"{names[c]}.wav", "Channel", wav, "pcm"));
        }
      }
      entries.Add(new("metadata.ini", "Tag", BuildMetadata(parsed.Info)));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      // Unparseable AST: surface FULL only.
    }

    return entries;
  }

  private static byte[] BuildMetadata(AstReader.Header info) {
    var codecName = info.Codec switch {
      0 => "AFC-ADPCM (not decoded)",
      1 => "PCM16BE",
      _ => $"unknown({info.Codec})",
    };
    var sb = new StringBuilder();
    sb.Append("[ast]\n");
    sb.Append(CultureInfo.InvariantCulture, $"sampleRate={info.SampleRate}\n");
    sb.Append(CultureInfo.InvariantCulture, $"channels={info.NumChannels}\n");
    sb.Append(CultureInfo.InvariantCulture, $"codec={codecName}\n");
    sb.Append(CultureInfo.InvariantCulture, $"sampleCount={info.SampleCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loop={(info.Loop ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loopStart={info.LoopStart}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loopEnd={info.LoopEnd}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ShortsToLe(short[] samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
