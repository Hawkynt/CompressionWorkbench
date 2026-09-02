#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Aiff;

/// <summary>
/// Exposes an AIFF / AIFC file as an archive of <c>FULL.aif</c>, one <c>LEFT.wav</c>/
/// <c>RIGHT.wav</c>/… per channel, plus <c>metadata/annotations.txt</c> and
/// <c>metadata/markers.bin</c>. Compressed AIFC payloads are decoded to linear PCM
/// before being split per channel (μ-law, A-law, <c>fl32</c>/<c>fl64</c> IEEE
/// float, and <c>ima4</c> Apple/QuickTime IMA ADPCM are decoded to per-channel PCM;
/// <c>GSM</c> is recognised but passed through as raw bytes in the <c>FULL.aif</c>
/// entry only).
/// </summary>
public sealed class AiffFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Aiff";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "AIFF / AIFC (Apple audio)";
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
  public string DefaultExtension => ".aif";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".aif", ".aiff", ".aifc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("FORM"u8.ToArray(), Confidence: 0.55),
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
  public string Description => "AIFF / AIFC audio; full file + per-channel PCM + markers + annotations.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "AIFF archive accepts: FULL.aif, LEFT/RIGHT/… .wav (per-channel), metadata/*.txt|bin";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";
    if (dir == "" && (name.EndsWith(".aif") || name.EndsWith(".aiff") || name.EndsWith(".aifc") || name.EndsWith(".wav"))) {
      reason = null; return true;
    }
    if (dir == "metadata") { reason = null; return true; }
    reason = $"not an AIFF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── IArchiveCreatable: assemble a multi-channel AIFF from per-channel mono WAVs ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // FULL.aif/.aiff/.aifc → passthrough verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f => {
      var n = Path.GetFileName(f.Name).ToLowerInvariant();
      return n is "full.aif" or "full.aiff" or "full.aifc";
    });
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("AIFF archive create needs either FULL.aif or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().ReadCanonicalPcm(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");

    var bytesPerSample = first.BitsPerSample / 8;
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    // Interleave the little-endian channels, then byte-swap each sample to the
    // big-endian order AIFF stores PCM in.
    var interleavedLe = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);
    var interleavedBe = SwapSampleEndianness(interleavedLe, bytesPerSample);

    var blob = new AiffWriter().Write(interleavedBe, channels.Count, first.SampleRate, first.BitsPerSample);
    output.Write(blob);
  }

  /// <summary>Reverses the byte order within each fixed-width sample (LE↔BE).</summary>
  private static byte[] SwapSampleEndianness(byte[] pcm, int bytesPerSample) {
    if (bytesPerSample <= 1) return pcm;
    var swapped = new byte[pcm.Length];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new AiffReader().Read(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.aif", "Container", blob),
    };

    // Decode to linear PCM (LE) if possible.
    var pcm = DecodeToPcm(parsed, out var bitsOut);
    if (pcm != null && bitsOut is 8 or 16 or 24 or 32 && parsed.NumChannels >= 1) {
      if (parsed.NumChannels == 1) {
        entries.Add(("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, parsed.SampleRate, bitsOut, formatCode: 1)));
      } else {
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
            pcm, parsed.NumChannels, parsed.SampleRate, bitsOut))
          entries.Add(($"{name}.wav", "Channel", wavBlob));
      }
    }

    if (parsed.Annotations != null && parsed.Annotations.Length > 0)
      entries.Add(("metadata/annotations.txt", "Tag", parsed.Annotations));
    if (parsed.Markers != null)
      entries.Add(("metadata/markers.bin", "Tag", parsed.Markers));
    if (parsed.Instrument != null)
      entries.Add(("metadata/instrument.bin", "Tag", parsed.Instrument));
    if (parsed.Id3 != null)
      entries.Add(("metadata/id3.bin", "Tag", parsed.Id3));
    foreach (var (id, data) in parsed.OtherChunks)
      entries.Add(($"metadata/{id.Trim()}.bin", "Tag", data));

    // Synthetic info file.
    var info = new StringBuilder();
    info.AppendLine($"format={(parsed.IsAifc ? "AIFC" : "AIFF")}");
    info.AppendLine($"compression_id={parsed.CompressionId}");
    info.AppendLine($"compression_name={parsed.CompressionName}");
    info.AppendLine($"channels={parsed.NumChannels}");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"bits_per_sample={parsed.BitsPerSample}");
    info.AppendLine($"sample_frames={parsed.SampleFrames}");
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  /// <summary>
  /// Decodes the AIFF/AIFC sound data to little-endian linear PCM appropriate for
  /// wrapping in a standard RIFF/WAVE file. Returns null when the compression isn't
  /// decodable to linear PCM (then only FULL.aif is exposed).
  /// </summary>
  private static byte[]? DecodeToPcm(AiffReader.ParsedAiff p, out int bitsOut) {
    bitsOut = p.BitsPerSample;
    var id = p.CompressionId;

    // Uncompressed AIFF (no AIFC compression ID) → big-endian PCM.
    if (!p.IsAifc || id == "NONE" || id == "twos") {
      return ConvertBigEndianPcmToLittleEndian(p.SoundData, p.BitsPerSample);
    }
    if (id == "sowt") {
      // Already little-endian.
      return p.SoundData.ToArray();
    }
    if (id == "ulaw" || id == "ULAW") {
      var decoded = Codec.MuLaw.MuLawCodec.Decode(p.SoundData);
      bitsOut = 16;
      return ShortsToLePcm(decoded);
    }
    if (id == "alaw" || id == "ALAW") {
      var decoded = Codec.ALaw.ALawCodec.Decode(p.SoundData);
      bitsOut = 16;
      return ShortsToLePcm(decoded);
    }
    if (id == "fl32" || id == "FL32") {
      bitsOut = 32;
      return BigEndianFloat32ToLeFloat32(p.SoundData);
    }
    if (id == "fl64" || id == "FL64") {
      bitsOut = 64;
      return BigEndianFloat64ToLeFloat64(p.SoundData);
    }
    if (id == "ima4") {
      // Apple/QuickTime IMA ADPCM: 34-byte packets, round-robin per channel.
      var channels = Math.Max(1, p.NumChannels);
      var perChannel = Codec.ImaAdpcm.ImaAdpcmCodec.DecodeQuickTime(p.SoundData, channels);
      var monoLe = perChannel.Select(ch => ShortsToLePcm(ch)).ToList();
      bitsOut = 16;
      return PcmCodec.Interleave(monoLe, 16);
    }
    // GSM: not decoded (would need a GSM 06.10 frame decoder).
    return null;
  }

  private static byte[] ConvertBigEndianPcmToLittleEndian(byte[] be, int bitsPerSample) {
    var bytesPerSample = bitsPerSample / 8;
    if (bytesPerSample <= 1) return (byte[])be.Clone();
    var le = new byte[be.Length];
    for (var i = 0; i < be.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample && i + j < be.Length; ++j)
        le[i + j] = be[i + bytesPerSample - 1 - j];
    return le;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static byte[] BigEndianFloat32ToLeFloat32(byte[] be) {
    var le = new byte[be.Length];
    for (var i = 0; i + 4 <= be.Length; i += 4) {
      le[i] = be[i + 3]; le[i + 1] = be[i + 2]; le[i + 2] = be[i + 1]; le[i + 3] = be[i];
    }
    return le;
  }

  private static byte[] BigEndianFloat64ToLeFloat64(byte[] be) {
    var le = new byte[be.Length];
    for (var i = 0; i + 8 <= be.Length; i += 8)
      for (var j = 0; j < 8; ++j) le[i + j] = be[i + 7 - j];
    return le;
  }
}
