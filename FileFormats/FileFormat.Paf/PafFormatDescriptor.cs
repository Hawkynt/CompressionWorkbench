#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Paf;

/// <summary>
/// Exposes an Ensoniq PARIS Audio File (.paf) as an archive of <c>FULL.paf</c> plus
/// one mono WAV per channel and a <c>metadata.ini</c>. Samples are normalised to
/// little-endian WAV PCM (big-endian files are byte-swapped; 24-bit packed 3-byte
/// samples surface as 24-bit WAVs). Mono surfaces as <c>MONO.wav</c>; multi-channel
/// files split into per-speaker WAVs.
/// </summary>
public sealed class PafFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Paf";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PARIS Audio File (Ensoniq)";
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
  public string DefaultExtension => ".paf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".paf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(" paf"u8.ToArray(), Offset: 0, Confidence: 0.90), // big-endian file
    new("fap "u8.ToArray(), Offset: 0, Confidence: 0.90), // little-endian file
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
  public string Description => "PARIS Audio File (Ensoniq .paf); full file + per-channel WAV.";

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

  // ── IArchiveCreatable: assemble a 16-bit LE "fap " PAF from per-channel mono WAVs ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.paf", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.paf", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("PAF archive create needs FULL.paf or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");
    if (first.BitsPerSample is not (8 or 16))
      throw new InvalidOperationException("PAF assembly accepts 8-bit or 16-bit mono WAVs.");

    var signed16 = channels.Select(c => ToSigned16Le(c.InterleavedPcm, c.BitsPerSample)).ToList();
    var interleavedLe = PcmCodec.Interleave(signed16, 16);

    var blob = new PafWriter().Write(interleavedLe, channels.Count, first.SampleRate);
    output.Write(blob);
  }

  /// <summary>WAV PCM (8-bit unsigned / 16-bit signed LE) → 16-bit signed little-endian.</summary>
  private static byte[] ToSigned16Le(byte[] pcm, int bitsPerSample) {
    if (bitsPerSample == 16) return (byte[])pcm.Clone();
    var r = new byte[pcm.Length * 2];
    for (var i = 0; i < pcm.Length; ++i) {
      var sample = (short)((pcm[i] - 128) << 8);
      BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(i * 2), sample);
    }
    return r;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "PAF archive accepts: FULL.paf, MONO/LEFT/RIGHT .wav (per-channel)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.paf" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a PAF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.paf", "Container", blob),
    };

    try {
      var parsed = new PafReader().Read(blob);
      var rate = parsed.SampleRate > 0 ? parsed.SampleRate : 8000;

      var decoded = DecodeToLePcm(parsed, out var bits);
      if (decoded != null && decoded.Length > 0) {
        if (parsed.NumChannels <= 1) {
          entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(decoded, 1, rate, bits)));
        } else {
          foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(decoded, parsed.NumChannels, rate, bits))
            entries.Add(new($"{name}.wav", "Channel", wavBlob));
        }
      }

      var info = new StringBuilder();
      info.AppendLine($"sample_rate={rate}");
      info.AppendLine($"channels={parsed.NumChannels}");
      info.AppendLine($"format={parsed.Format} ({FormatName(parsed.Format)})");
      info.AppendLine($"endianness={(parsed.LittleEndian ? "little" : "big")}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    } catch (InvalidDataException) {
      // Graceful FULL-only fallback for malformed / unsupported PAF files.
    }

    return entries;
  }

  /// <summary>
  /// PAF samples → canonical little-endian WAV PCM. 16-bit big-endian is byte-swapped;
  /// 24-bit packed 3-byte LSB-first samples are already little-endian. Returns null for
  /// unsupported formats so the descriptor falls back to FULL-only.
  /// </summary>
  private static byte[]? DecodeToLePcm(PafReader.ParsedPaf p, out int bits) {
    switch (p.Format) {
      case PafReader.FormatPcm16:
        bits = 16;
        return p.LittleEndian ? p.Data : SwapEndianness(p.Data, 2);
      case PafReader.FormatPcm24:
        bits = 24;
        // 24-bit PARIS samples are packed 3 bytes; big-endian files store them
        // most-significant byte first, so reverse each 3-byte group to little-endian.
        return p.LittleEndian ? p.Data : SwapEndianness(p.Data, 3);
      default:
        bits = 16;
        return null;
    }
  }

  private static byte[] SwapEndianness(byte[] pcm, int bytesPerSample) {
    if (bytesPerSample <= 1) return pcm;
    var swapped = new byte[pcm.Length];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }

  private static string FormatName(int f) => f switch {
    PafReader.FormatPcm16 => "16-bit PCM",
    PafReader.FormatPcm24 => "24-bit PCM",
    _ => $"unsupported ({f})",
  };
}
