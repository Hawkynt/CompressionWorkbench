#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.CriHca;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Hca;

/// <summary>
/// Archive-shaped view of a CRI HCA file (<c>.hca</c>, modern CRI Middleware game audio):
/// a byte-exact <c>FULL.hca</c> container plus one decoded mono PCM WAV per channel
/// (named per <see cref="ChannelLayout"/>) and a <c>metadata.ini</c> carrying the
/// stream's sample rate, channel count, frame count, loop and cipher info. Decoding goes
/// through the in-repo <see cref="HcaCodec"/>; when the codec cannot handle the input
/// (keyed/56-bit cipher, MS-stereo, malformed or CRC-failing headers) the view degrades
/// gracefully to <c>FULL.hca</c> plus a <c>metadata.ini</c> note. READ-ONLY.
/// </summary>
public sealed class HcaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Hca";
  public string DisplayName => "CRI HCA";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".hca";
  public IReadOnlyList<string> Extensions => [".hca"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "HCA\0" plain, and the per-byte 0x80-masked variant used by keyed streams ("HCA" → C8 C3 C1).
    new([0x48, 0x43, 0x41, 0x00], Confidence: 0.95),
    new([0xC8, 0xC3, 0xC1], Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hca", "CRI HCA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CRI HCA (High Compression Audio); full file + decoded per-channel PCM.";

  // ── IArchiveFormatOperations ─────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  // ── IArchiveInMemoryExtract ──────────────────────────────────────────

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── Shared archive-entry builder ─────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.hca", "Container", blob, "hca"),
    };

    // Parse the header first so metadata is surfaced even when the audio cannot be decoded
    // (keyed cipher / MS-stereo). A malformed header falls back to FULL-only.
    HcaCodec.HcaHeader header;
    try {
      (header, _) = HcaCodec.ReadHeader(blob);
    } catch (Exception) {
      return entries; // not a parseable HCA — surface the container only
    }

    var note = header.IsKeyedCipher
      ? "note=keyed (56-bit) cipher; audio not decoded (FULL.hca + metadata only)."
      : header.IsMsStereo
        ? "note=MS-stereo stream; audio not decoded (FULL.hca + metadata only)."
        : null;

    if (note == null) {
      try {
        var (samples, channels, sampleRate, _) = HcaCodec.Decode(blob);
        var pcm = ShortsToLePcm(samples);
        const int bitsPerSample = 16;

        if (channels == 1) {
          entries.Add(new("MONO.wav", "Channel",
            PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample, formatCode: 1), "pcm"));
        } else {
          foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, bitsPerSample))
            entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
        }
      } catch (Exception) {
        note = "note=audio decode failed; FULL.hca + metadata only.";
      }
    }

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadata(header, note))));
    return entries;
  }

  private static string BuildMetadata(HcaCodec.HcaHeader h, string? note) {
    var meta = new StringBuilder();
    meta.AppendLine($"version=0x{h.Version:X4}");
    meta.AppendLine($"sample_rate={h.SampleRate}");
    meta.AppendLine($"channels={h.Channels}");
    meta.AppendLine($"frame_count={h.FrameCount}");
    meta.AppendLine($"total_samples={h.TotalSamples}");
    meta.AppendLine($"frame_size={h.FrameSize}");
    meta.AppendLine($"total_band_count={h.TotalBandCount}");
    meta.AppendLine($"base_band_count={h.BaseBandCount}");
    meta.AppendLine($"stereo_band_count={h.StereoBandCount}");
    meta.AppendLine($"ath_type={h.AthType}");
    meta.AppendLine($"cipher_type={h.CipherType}");
    meta.AppendLine($"ms_stereo={(h.IsMsStereo ? 1 : 0)}");
    meta.AppendLine($"loop={(h.HasLoop ? 1 : 0)}");
    if (h.HasLoop) {
      meta.AppendLine($"loop_start_frame={h.LoopStartFrame}");
      meta.AppendLine($"loop_end_frame={h.LoopEndFrame}");
    }
    if (!string.IsNullOrEmpty(h.Comment))
      meta.AppendLine($"comment={h.Comment}");
    if (note != null)
      meta.AppendLine(note);
    return meta.ToString();
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
