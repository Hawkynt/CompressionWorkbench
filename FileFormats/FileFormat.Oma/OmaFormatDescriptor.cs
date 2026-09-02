#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Atrac3;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Oma;

/// <summary>
/// Read-only stream-info view of a Sony OpenMG (.oma / .aa3 / .at3) file. There is no ATRAC
/// decoder (out of scope); the descriptor parses the leading "ea3" ID3v2-style tag (TIT2 / TPE1
/// / TALB … text frames) and the 96-byte binary "EA3" header (codec id + coding parameters),
/// then slices out the coded audio payload. The archive view surfaces the byte-exact
/// <c>FULL.oma</c> (Kind <c>Container</c>), the coded <c>stream.bin</c> payload (Kind
/// <c>Stream</c>, Method = the carried codec name) and <c>metadata.ini</c> (Kind <c>Tag</c>).
/// </summary>
public sealed class OmaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Oma";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Sony OpenMG (OMA/AA3)";
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
  public string DefaultExtension => ".oma";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".oma", ".aa3", ".at3"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // The leading "ea3" ID3v2-style tag identifier (the binary "EA3" header is at a variable offset).
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(OmaHeader.TagMagic, Confidence: 0.90),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("oma", "OpenMG")];
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
  public string Description => "Sony OpenMG (ATRAC3/3plus) container; tag + stream info (no decode).";

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
      new("FULL.oma", "Container", blob, "oma"),
    };

    var header = OmaHeader.TryParse(blob);
    var info = new StringBuilder();
    if (header is null) {
      info.AppendLine("codec=unknown");
      info.AppendLine("note=no parseable OpenMG ea3/EA3 header found.");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
      return entries;
    }

    // Surface the coded payload after the EA3 header (Method = carried codec name).
    var payloadLength = blob.Length - header.PayloadOffset;
    if (payloadLength > 0) {
      var payload = new byte[payloadLength];
      Array.Copy(blob, header.PayloadOffset, payload, 0, payloadLength);
      entries.Add(new("stream.bin", "Stream", payload, header.CodecName));

      // ATRAC3 (codec id 0): decode the coded payload to per-channel WAVs. Falls back to
      // the blob-only view on any decode failure (unsupported params / truncation).
      if (header.CodecId == 0)
        AddDecodedAtrac3Channels(payload, header.CodingParams, header.SampleRate, entries);
    }

    info.AppendLine($"codec={header.CodecName}");
    info.AppendLine($"codec_id={header.CodecId}");
    info.AppendLine($"coding_params=0x{header.CodingParams:X6}");
    if (header.SampleRate > 0)
      info.AppendLine($"sample_rate={header.SampleRate}");
    info.AppendLine($"tag_size={header.TagSize}");
    info.AppendLine($"ea3_header_offset={header.Ea3HeaderOffset}");
    info.AppendLine($"payload_offset={header.PayloadOffset}");
    info.AppendLine($"payload_bytes={payloadLength}");
    foreach (var (frame, value) in header.Tags)
      info.AppendLine($"{frame}={value}");

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    return entries;
  }

  /// <summary>
  /// Decodes the ATRAC3 (codec id 0) coded payload into per-channel mono WAV entries
  /// (Kind <c>Channel</c>). Coding parameters (block align, joint-stereo, sample rate) are
  /// derived from the EA3 24-bit coding-params field. Any decode failure leaves only the
  /// stream blob, matching the descriptor's pre-existing behaviour.
  /// </summary>
  private static void AddDecodedAtrac3Channels(byte[] payload, int codingParams, int sampleRate,
      List<AudioPseudoArchive.Entry> entries) {
    try {
      var codec = Atrac3Codec.FromOmaCodingParams(codingParams);
      if (codec.BlockAlign <= 0 || payload.Length < codec.BlockAlign)
        return;

      var interleaved = codec.DecodeStream(payload);
      if (interleaved.Length == 0)
        return;

      var rate = sampleRate > 0 ? sampleRate : codec.SampleRate;
      var le = new byte[interleaved.Length * 2];
      for (var i = 0; i < interleaved.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), interleaved[i]);

      var channels = PcmCodec.SplitInterleavedPcm(le, codec.Channels, rate, bitsPerSample: 16);
      foreach (var (name, wav) in channels)
        entries.Add(new($"{name}.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Unsupported ATRAC3 params / truncated payload — keep the stream blob only.
    }
  }
}
