#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Codec.Tta;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Tta;

/// <summary>
/// Exposes a True Audio (<c>.tta</c>) file as a pseudo-archive of <c>FULL.tta</c>
/// (Kind <c>Container</c>) plus, when the bitstream decodes, one mono WAV per
/// channel (Kind <c>Channel</c>, named via <see cref="ChannelLayout"/>) and a
/// <c>metadata.ini</c> (Kind <c>Tag</c>). Decode failures degrade gracefully to a
/// FULL-only listing. The descriptor is also creatable (WORM): it assembles a
/// fresh <c>.tta</c> either by passing through a supplied <c>FULL.tta</c> or by
/// interleaving per-channel mono WAVs and encoding them with <see cref="TtaCodec"/>.
/// </summary>
public sealed class TtaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Tta";
  public string DisplayName => "True Audio (TTA)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tta";
  public IReadOnlyList<string> Extensions => [".tta"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("TTA1"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tta", "TTA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  public string Description => "True Audio (TTA) lossless audio; full file + decoded per-channel PCM.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveWriteConstraints ────────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "TTA archive accepts: FULL.tta, LEFT/RIGHT/… .wav (per-channel), metadata.ini";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.tta" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a TTA-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── IArchiveCreatable: pass through FULL.tta or assemble from per-channel WAVs ──

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.tta", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("TTA archive create needs either FULL.tta or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);

    using var pcm = new MemoryStream(interleaved);
    TtaCodec.Compress(pcm, output, channels.Count, first.SampleRate, first.BitsPerSample);
  }

  // ── Shared archive-entry builder ─────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.tta", "Container", blob, "tta"),
    };

    // Best-effort decode-and-split; on any failure keep the FULL-only listing.
    try {
      using var probe = new MemoryStream(blob, writable: false);
      var info = TtaCodec.ReadStreamInfo(probe);

      using var src = new MemoryStream(blob, writable: false);
      using var pcm = new MemoryStream();
      TtaCodec.Decompress(src, pcm);
      var pcmBytes = pcm.ToArray();

      if (info.Channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcmBytes, 1, info.SampleRate, info.BitsPerSample, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcmBytes, info.Channels, info.SampleRate, info.BitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }

      var meta = new StringBuilder();
      meta.AppendLine("format=TTA1");
      meta.AppendLine($"channels={info.Channels}");
      meta.AppendLine($"sample_rate={info.SampleRate}");
      meta.AppendLine($"bits_per_sample={info.BitsPerSample}");
      meta.AppendLine($"samples_per_channel={info.SampleCount}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString()), "stored"));
    } catch (Exception) {
      // Graceful fallback: surface the original TTA file only.
    }

    return entries;
  }
}
