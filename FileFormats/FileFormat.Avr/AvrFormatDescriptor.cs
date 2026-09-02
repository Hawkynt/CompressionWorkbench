#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Avr;

/// <summary>
/// Exposes an AVR (Audio Visual Research) file as an archive of <c>FULL.avr</c> plus
/// one mono WAV per channel and a <c>metadata.ini</c>. The on-disk samples are
/// normalised to canonical PCM (16-bit big-endian → little-endian; sign-converted so
/// the WAV is 8-bit unsigned / 16-bit signed). Mono surfaces as <c>MONO.wav</c>;
/// stereo as <c>LEFT.wav</c> / <c>RIGHT.wav</c>.
/// </summary>
public sealed class AvrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Avr";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "AVR (Audio Visual Research)";
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
  public string DefaultExtension => ".avr";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".avr"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("2BIT"u8.ToArray(), Confidence: 0.90),
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
  public string Description => "AVR (Audio Visual Research, Atari ST); full file + per-channel WAV.";

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

  // ── IArchiveCreatable: assemble a 16-bit signed BE AVR from per-channel mono WAVs ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.avr", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.avr", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count is 0 or > 2)
      throw new InvalidOperationException("AVR archive create needs FULL.avr or one (mono) / two (stereo) per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");
    if (first.BitsPerSample is not (8 or 16))
      throw new InvalidOperationException("AVR assembly accepts 8-bit or 16-bit mono WAVs.");

    // Normalise every channel to 16-bit signed little-endian, interleave, then swap to BE.
    var signed16 = channels.Select(c => ToSigned16Le(c.InterleavedPcm, c.BitsPerSample)).ToList();
    var interleavedLe = PcmCodec.Interleave(signed16, 16);
    var interleavedBe = SwapEndianness(interleavedLe, 2);

    var blob = new AvrWriter().Write(interleavedBe, channels.Count, first.SampleRate, "");
    output.Write(blob);
  }

  /// <summary>WAV PCM (8-bit unsigned / 16-bit signed) → 16-bit signed little-endian.</summary>
  private static byte[] ToSigned16Le(byte[] pcm, int bitsPerSample) {
    if (bitsPerSample == 16) return (byte[])pcm.Clone();
    // 8-bit unsigned → 16-bit signed: (b - 128) << 8.
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
    "AVR archive accepts: FULL.avr, MONO/LEFT/RIGHT .wav (per-channel)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.avr" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not an AVR-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new AvrReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.avr", "Container", blob),
    };

    var rate = parsed.SampleRate > 0 ? parsed.SampleRate : 8000;
    var pcm = NormalisePcm(parsed);
    if (pcm.Length > 0 && parsed.NumChannels >= 1) {
      if (parsed.NumChannels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, 1, rate, parsed.BitsPerSample)));
      } else {
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
            pcm, parsed.NumChannels, rate, parsed.BitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wavBlob));
      }
    }

    var info = new StringBuilder();
    if (!string.IsNullOrEmpty(parsed.Name)) info.AppendLine($"name={parsed.Name}");
    info.AppendLine($"sample_rate={rate}");
    info.AppendLine($"channels={parsed.NumChannels}");
    info.AppendLine($"bits={parsed.BitsPerSample}");
    info.AppendLine($"loop_begin={parsed.LoopBegin}");
    info.AppendLine($"loop_end={parsed.LoopEnd}");
    if (!string.IsNullOrEmpty(parsed.User)) info.AppendLine($"user={parsed.User}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  /// <summary>
  /// AVR samples → canonical WAV PCM: 16-bit big-endian is byte-swapped to
  /// little-endian, then sign is converted so WAV gets 8-bit unsigned / 16-bit signed.
  /// </summary>
  private static byte[] NormalisePcm(AvrReader.ParsedAvr p) {
    if (p.BitsPerSample == 8) {
      // WAV wants 8-bit unsigned: rebias when the source is signed.
      if (!p.Signed) return (byte[])p.SampleData.Clone();
      var r = new byte[p.SampleData.Length];
      for (var i = 0; i < r.Length; ++i)
        r[i] = unchecked((byte)(p.SampleData[i] + 128));
      return r;
    }

    // 16-bit: big-endian → little-endian.
    var le = SwapEndianness(p.SampleData, 2);
    if (p.Signed) return le;
    // WAV wants 16-bit signed: flip the sign bit of each unsigned sample.
    for (var i = 0; i + 2 <= le.Length; i += 2) {
      var v = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(le.AsSpan(i)) ^ 0x8000);
      BinaryPrimitives.WriteUInt16LittleEndian(le.AsSpan(i), v);
    }
    return le;
  }
}
