#pragma warning disable CS1591
using System.Text;
using Codec.Nellymoser;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Nelly;

/// <summary>
/// Surfaces a raw Nellymoser "Asao" block stream (as dumped from FLV audio tags) as
/// a pseudo-archive: the byte-exact <c>FULL.nelly</c> container, the single decoded
/// mono <c>MONO.wav</c> (Nellymoser is fixed mono — 64-byte blocks → 256 samples
/// each, via <see cref="NellymoserCodec"/>), and a <c>metadata.ini</c>. Read-only;
/// there is no published encoder.
/// <para>
/// The stream is headerless and identified by extension only. There is no embedded
/// sample rate, so a default of 22050 Hz is assumed (the common Flash speech rate)
/// and recorded in <c>metadata.ini</c>. A ragged stream whose length is not a
/// multiple of the 64-byte block falls back to <c>FULL.nelly</c> + metadata only.
/// </para>
/// </summary>
public sealed class NellyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  private const int BlockLen = 64;
  private const int DefaultSampleRate = 22050;

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Nelly";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Nellymoser Asao (Flash audio stream)";
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
public string DefaultExtension => ".nelly";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".nelly"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // Headerless raw stream — identified by extension only, no magic signature.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
public string Description => "Nellymoser Asao raw block stream; full file + decoded mono WAV.";

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
      new("FULL.nelly", "Container", blob),
    };

    var decoded = TryDecode(blob);
    if (decoded != null) {
      var pcm = ShortsToLePcm(decoded);
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcm, channels: 1, DefaultSampleRate, bitsPerSample: 16), "nellymoser"));
    }

    entries.Add(new("metadata.ini", "Tag", BuildMetadata(blob, decoded != null)));
    return entries;
  }

  private static short[]? TryDecode(byte[] blob) {
    // Ragged tail (not a whole number of 64-byte blocks) → FULL-only fallback.
    if (blob.Length == 0 || blob.Length % BlockLen != 0)
      return null;
    try {
      var samples = NellymoserCodec.Decode(blob, DefaultSampleRate);
      return samples.Length == 0 ? null : samples;
    } catch {
      return null;
    }
  }

  private static byte[] BuildMetadata(byte[] blob, bool decoded) {
    var sb = new StringBuilder();
    sb.AppendLine("; Nellymoser Asao raw stream (headerless)");
    sb.Append("blocks=").AppendLine((blob.Length / BlockLen).ToString(System.Globalization.CultureInfo.InvariantCulture));
    sb.Append("trailing_bytes=").AppendLine((blob.Length % BlockLen).ToString(System.Globalization.CultureInfo.InvariantCulture));
    sb.AppendLine("channels=1");
    sb.Append("assumed_sample_rate=").AppendLine(DefaultSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
    sb.Append("decoded=").AppendLine(decoded ? "true" : "false");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
