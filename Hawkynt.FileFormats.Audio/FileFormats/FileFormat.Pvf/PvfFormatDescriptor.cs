#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Pvf;

/// <summary>
/// Exposes an mgetty Portable Voice Format (.pvf) file as an archive of <c>FULL.pvf</c>
/// plus one mono WAV per channel and a <c>metadata.ini</c>. The container's
/// arbitrary-width samples are shifted to canonical 16-bit signed little-endian PCM
/// (widths &gt; 16 shift down, &lt; 16 shift up). Mono surfaces as <c>MONO.wav</c>;
/// multi-channel (interleaved) files split into per-speaker WAVs.
/// </summary>
public sealed class PvfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Pvf";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Portable Voice Format (mgetty)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".pvf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pvf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PVF1"u8.ToArray(), Confidence: 0.90),
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
  public string Description => "Portable Voice Format (mgetty .pvf); full file + per-channel WAV.";

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

  // ── IArchiveCreatable: assemble a PVF1 (bits=16) file from per-channel mono WAVs ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.pvf", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.pvf", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("PVF archive create needs FULL.pvf or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().ReadCanonicalPcm(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");
    if (first.BitsPerSample != 16)
      throw new InvalidOperationException("PVF assembly accepts 16-bit mono WAVs.");

    var signed16 = channels.Select(c => c.InterleavedPcm).ToList();
    var interleavedLe = PcmCodec.Interleave(signed16, 16);
    var samples = LeToShorts(interleavedLe);

    var blob = new PvfWriter().Write(samples, channels.Count, first.SampleRate);
    output.Write(blob);
  }

  private static short[] LeToShorts(byte[] le) {
    var samples = new short[le.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(le.AsSpan(i * 2));
    return samples;
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
    "PVF archive accepts: FULL.pvf, MONO/LEFT/RIGHT .wav (per-channel)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.pvf" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a PVF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.pvf", "Container", blob),
    };

    try {
      var parsed = new PvfReader().Read(blob);
      var rate = parsed.SampleRate > 0 ? parsed.SampleRate : 8000;
      var pcmLe = ToLe16Pcm(parsed.Samples, parsed.Bits);

      if (pcmLe.Length > 0) {
        if (parsed.NumChannels <= 1) {
          entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcmLe, 1, rate, 16)));
        } else {
          foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(pcmLe, parsed.NumChannels, rate, 16))
            entries.Add(new($"{name}.wav", "Channel", wavBlob));
        }
      }

      var info = new StringBuilder();
      info.AppendLine($"sample_rate={rate}");
      info.AppendLine($"channels={parsed.NumChannels}");
      info.AppendLine($"bits={parsed.Bits}");
      info.AppendLine($"encoding={(parsed.Ascii ? "ascii (PVF2)" : "binary (PVF1)")}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    } catch (InvalidDataException) {
      // Graceful FULL-only fallback for malformed / unsupported PVF files.
    } catch (FormatException) {
      // Non-numeric header / sample tokens → FULL-only fallback.
    }

    return entries;
  }

  /// <summary>
  /// Shifts PVF samples (significant in their original <paramref name="bits"/> width)
  /// to 16-bit signed little-endian PCM: widths above 16 shift down, below 16 shift up.
  /// </summary>
  private static byte[] ToLe16Pcm(int[] samples, int bits) {
    var pcm = new byte[samples.Length * 2];
    var shift = bits - 16;
    for (var i = 0; i < samples.Length; ++i) {
      var v = samples[i];
      var scaled = shift > 0 ? v >> shift : v << -shift;
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)scaled);
    }
    return pcm;
  }
}
