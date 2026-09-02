#pragma warning disable CS1591
using System.Text;
using Codec.InterplayAcm;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Acm;

/// <summary>
/// Surfaces an Interplay ACM file (Fallout, Baldur's Gate, … — magic
/// <c>0x01032897</c>) as a pseudo-archive: the byte-exact <c>FULL.acm</c> container,
/// one decoded mono <c>&lt;CHANNEL&gt;.wav</c> per channel (via
/// <see cref="InterplayAcmCodec"/>), and a <c>metadata.ini</c> carrying the parsed
/// header. The format is read-only (there is no published ACM encoder).
/// <para>
/// Interplay assets are quirky: the header's channel field is often <c>1</c> even
/// for material that ships interleaved as stereo (and many ACMs are wrapped inside
/// <c>.bif</c> archives). The descriptor surfaces the raw header value verbatim in
/// <c>metadata.ini</c> and splits exactly that many channels — callers that know an
/// asset is really stereo can re-interpret the single decoded stream. Inputs the
/// decoder can't handle fall back to <c>FULL.acm</c> + metadata only.
/// </para>
/// </summary>
public sealed class AcmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Acm";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Interplay ACM (Fallout / Baldur's Gate audio)";
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
  public string DefaultExtension => ".acm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".acm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x97, 0x28, 0x03, 0x01], Confidence: 0.95),
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
  public string Description => "Interplay ACM audio; full file + decoded per-channel WAVs.";

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

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.acm", "Container", blob),
    };

    AddDecodedChannels(blob, entries);
    entries.Add(new("metadata.ini", "Tag", BuildMetadata(blob)));
    return entries;
  }

  private static void AddDecodedChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    try {
      var (samples, channels, sampleRate) = InterplayAcmCodec.Decode(blob);
      if (samples.Length == 0)
        return;

      var pcm = ShortsToLePcm(samples);
      if (channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample: 16), "interplay-acm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "interplay-acm"));
      }
    } catch {
      // Undecodable (corrupt/unsupported) — FULL.acm + metadata only.
    }
  }

  private static byte[] BuildMetadata(byte[] blob) {
    var sb = new StringBuilder();
    sb.AppendLine("; Interplay ACM header");
    try {
      var h = InterplayAcmCodec.ParseHeader(blob);
      sb.Append("total_samples=").AppendLine(h.TotalSamples.ToString(System.Globalization.CultureInfo.InvariantCulture));
      sb.Append("channels=").AppendLine(h.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
      sb.Append("sample_rate=").AppendLine(h.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
      sb.Append("level=").AppendLine(h.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
      sb.Append("rows=").AppendLine(h.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
      sb.AppendLine("; note: the channels field is the raw header value; many Interplay");
      sb.AppendLine("; assets report 1 even for interleaved stereo content.");
    } catch {
      sb.AppendLine("; header could not be parsed");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
