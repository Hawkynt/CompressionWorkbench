#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Mace;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.MacSnd;

/// <summary>
/// Exposes a classic Mac OS <c>'snd '</c> sampled-sound resource (carried as a data
/// fork) as an archive of <c>FULL.snd</c> plus one mono WAV per channel and a
/// <c>metadata.ini</c>. Standard (8-bit unsigned), extended (8/16-bit, mono/stereo)
/// and MACE-compressed (3:1 / 6:1, decoded via <c>Codec.Mace</c>) sound headers are
/// supported; unsupported compression IDs surface only <c>FULL.snd</c> + metadata.
/// <para>The <c>.snd</c> extension is shared with Sun/NeXT <c>.au</c>, so this format
/// registers no file extension and is reached by magic (format-1 sampled resource) or
/// explicit lookup.</para>
/// READ-ONLY.
/// </summary>
public sealed class MacSndFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "MacSnd";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Mac 'snd ' resource";
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
public string DefaultExtension => ".snd";
  // ".snd" collides with the Sun/NeXT .au format; rely on magic + explicit lookup.
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // format 1 | nDataFormats=1 | firstDataFormatId=5 (sampledSynth)
    new([0x00, 0x01, 0x00, 0x01, 0x00, 0x05], Confidence: 0.70),
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
public string Description => "Classic Mac OS 'snd ' resource; standard / extended / MACE-compressed → per-channel WAV.";

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

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new MacSndReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.snd", "Container", blob),
    };

    var rate = parsed.SampleRate > 0 ? parsed.SampleRate : 22050;
    var (pcm, bitsOut, channels, note) = DecodeToPcm(parsed);

    if (pcm is { Length: > 0 } && channels >= 1) {
      if (channels == 1) {
        entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, rate, bitsOut)));
      } else {
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(pcm, channels, rate, bitsOut))
          entries.Add(new($"{name}.wav", "Channel", wavBlob));
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"format={parsed.Format}");
    info.AppendLine($"encode={EncodeName(parsed.Encode)}");
    info.AppendLine($"sample_rate={rate}");
    info.AppendLine($"channels={channels}");
    info.AppendLine($"bits={bitsOut}");
    info.AppendLine($"num_frames={parsed.NumFrames}");
    if (parsed.Encode == MacSndReader.CompressedHeader)
      info.AppendLine($"compression_id={parsed.CompressionId}");
    if (!string.IsNullOrEmpty(note))
      info.AppendLine($"note={note}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static (byte[]? pcm, int bits, int channels, string note) DecodeToPcm(MacSndReader.ParsedSnd p) {
    switch (p.Encode) {
      case MacSndReader.StandardHeader:
        // 8-bit unsigned PCM, mono — WAV-ready as-is.
        return (p.SampleData, 8, 1, "");

      case MacSndReader.ExtendedHeader:
        if (p.BitsPerSample == 16) {
          // 16-bit signed big-endian → little-endian.
          return (SwapEndianness(p.SampleData, 2), 16, p.NumChannels, "");
        }
        // 8-bit unsigned PCM (interleaved when stereo).
        return (p.SampleData, 8, p.NumChannels, "");

      case MacSndReader.CompressedHeader:
        switch (p.CompressionId) {
          case MacSndReader.CompressionMace3: {
            var decoded = MaceCodec.DecodeMace3(p.SampleData, p.NumChannels);
            return (ShortsToLePcm(decoded), 16, p.NumChannels, "MACE 3:1 decoded");
          }
          case MacSndReader.CompressionMace6: {
            var decoded = MaceCodec.DecodeMace6(p.SampleData, p.NumChannels);
            return (ShortsToLePcm(decoded), 16, p.NumChannels, "MACE 6:1 decoded");
          }
          default:
            return (null, 0, p.NumChannels, $"unsupported compressionID {p.CompressionId}");
        }

      default:
        return (null, 0, 1, "unsupported encode");
    }
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static byte[] SwapEndianness(byte[] pcm, int bytesPerSample) {
    if (bytesPerSample <= 1) return (byte[])pcm.Clone();
    var swapped = new byte[pcm.Length - pcm.Length % bytesPerSample];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }

  private static string EncodeName(byte encode) => encode switch {
    MacSndReader.StandardHeader => "standard (8-bit unsigned)",
    MacSndReader.ExtendedHeader => "extended",
    MacSndReader.CompressedHeader => "compressed",
    _ => $"unknown (0x{encode:X2})",
  };
}
