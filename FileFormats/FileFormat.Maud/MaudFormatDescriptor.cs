#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Maud;

/// <summary>
/// Exposes an IFF / MAUD file as an archive of <c>FULL.maud</c> plus one mono WAV per
/// channel (16-bit big-endian PCM is byte-swapped to little-endian; A-law / μ-law data
/// is decoded to 16-bit linear via <c>Codec.ALaw</c> / <c>Codec.MuLaw</c>), plus a
/// <c>metadata.ini</c>. Mono surfaces as <c>MONO.wav</c>; stereo as <c>LEFT.wav</c> /
/// <c>RIGHT.wav</c> by de-interleaving the sample data.
/// </summary>
public sealed class MaudFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Maud";
  public string DisplayName => "IFF/MAUD (MacroSystem)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".maud";
  public IReadOnlyList<string> Extensions => [".maud"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // FORM at offset 0 is shared with other IFF registrations; the form type at offset 8
  // ("MAUD") is the discriminator that identifies this format.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MAUD"u8.ToArray(), Offset: 8, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "IFF/MAUD (MacroSystem audio); full file + per-channel WAV (A-law/μ-law decoded).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: assemble an uncompressed 16-bit BE MAUD from per-channel mono WAVs ──

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.maud", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.maud", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count is 0 or > 2)
      throw new InvalidOperationException("MAUD archive create needs FULL.maud or one (mono) / two (stereo) per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");
    if (first.BitsPerSample is not (8 or 16))
      throw new InvalidOperationException("MAUD assembly accepts 8-bit or 16-bit mono WAVs.");

    // Normalise every channel to 16-bit signed little-endian, interleave, then swap to BE.
    var signed16 = channels.Select(c => ToSigned16Le(c.InterleavedPcm, c.BitsPerSample)).ToList();
    var interleavedLe = PcmCodec.Interleave(signed16, 16);
    var interleavedBe = SwapEndianness(interleavedLe, 2);

    var blob = new MaudWriter().Write(interleavedBe, channels.Count, first.SampleRate);
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

  private static byte[] SwapEndianness(byte[] pcm, int bytesPerSample) {
    if (bytesPerSample <= 1) return pcm;
    var swapped = new byte[pcm.Length];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "MAUD archive accepts: FULL.maud, MONO/LEFT/RIGHT .wav (per-channel)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.maud" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a MAUD-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.maud", "Container", blob),
    };

    try {
      var parsed = new MaudReader().Read(blob);
      var rate = parsed.SampleRate > 0 ? parsed.SampleRate : 8000;
      var (pcmLe, bits) = DecodeToLePcm(parsed);

      if (pcmLe.Length > 0) {
        var channels = parsed.ChannelInfo == MaudReader.ChannelInfoStereo || parsed.NumChannels > 1
          ? Math.Max(2, parsed.NumChannels)
          : 1;
        if (channels == 1) {
          entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcmLe, 1, rate, bits)));
        } else {
          foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(pcmLe, channels, rate, bits))
            entries.Add(new($"{name}.wav", "Channel", wavBlob));
        }
      }

      var info = new StringBuilder();
      info.AppendLine($"sample_rate={rate}");
      info.AppendLine($"channels={parsed.NumChannels}");
      info.AppendLine($"channel_info={ChannelInfoName(parsed.ChannelInfo)}");
      info.AppendLine($"bits={parsed.BitsUncompressed}");
      info.AppendLine($"compression={parsed.Compression} ({CompressionName(parsed.Compression)})");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    } catch (InvalidDataException) {
      // Graceful FULL-only fallback for malformed / unsupported MAUD files.
    }

    return entries;
  }

  /// <summary>
  /// MAUD samples → canonical little-endian WAV PCM. Uncompressed 16-bit is
  /// byte-swapped from big-endian; uncompressed 8-bit is signed PCM rebiased to WAV's
  /// unsigned 8-bit; A-law / μ-law is decoded to 16-bit linear LE PCM.
  /// </summary>
  private static (byte[] Pcm, int Bits) DecodeToLePcm(MaudReader.ParsedMaud p) {
    switch (p.Compression) {
      case MaudReader.CompressionALaw: {
        var shorts = Codec.ALaw.ALawCodec.Decode(p.Data);
        return (ShortsToLePcm(shorts), 16);
      }
      case MaudReader.CompressionULaw: {
        var shorts = Codec.MuLaw.MuLawCodec.Decode(p.Data);
        return (ShortsToLePcm(shorts), 16);
      }
      default:
        if (p.BitsUncompressed <= 8) {
          // Signed 8-bit PCM → WAV unsigned 8-bit.
          var r = new byte[p.Data.Length];
          for (var i = 0; i < r.Length; ++i)
            r[i] = unchecked((byte)(p.Data[i] + 128));
          return (r, 8);
        }
        // 16-bit big-endian → little-endian.
        return (SwapEndianness(p.Data, 2), 16);
    }
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static string CompressionName(int c) => c switch {
    MaudReader.CompressionNone => "none (signed PCM)",
    MaudReader.CompressionALaw => "A-law",
    MaudReader.CompressionULaw => "μ-law",
    _ => $"unknown ({c})",
  };

  private static string ChannelInfoName(int c) => c switch {
    MaudReader.ChannelInfoMono => "mono",
    MaudReader.ChannelInfoStereo => "stereo",
    _ => $"unknown ({c})",
  };
}
