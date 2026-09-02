#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Codec.Siren;
using Compression.Registry;

namespace FileFormat.Siren;

/// <summary>
/// Raw Siren7 / ITU-T G.722.1 container surfaced as a pseudo-archive. Siren7 is a headerless stream
/// of fixed-size frames (each frame decodes to <see cref="SirenCodec.FrameSize"/> = 320 samples at
/// 16 kHz mono); the frame size in bytes is set by the encoder's bitrate and is <b>not</b> stored
/// in the stream. Raw <c>.sir</c> / <c>.g7221</c> files are uncommon and carry no magic, so dispatch
/// is extension-only and detection is low-confidence structural (the byte length must be a whole
/// multiple of the assumed frame size).
/// <para>
/// The default assumed frame size is <see cref="DefaultFrameBytes"/> (60 bytes ≙ 24 kbit/s at the
/// 50 frame/s Siren7 rate). The archive view surfaces <c>FULL.g7221</c> (the byte-exact stream,
/// Kind <c>Container</c>), <c>MONO.wav</c> (the decoded 16-bit PCM, Kind <c>Channel</c>) and
/// <c>metadata.ini</c> (Kind <c>Tag</c>). Read-only: G.722.1 has no encoder here.
/// </para>
/// <para><b>Scope:</b> only Siren7 / G.722.1 (16 kHz, 14 regions) is decoded; G.722.1 Annex C
/// (Siren14, 32 kHz) is not supported by the underlying codec.</para>
/// </summary>
public sealed class SirenFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  /// <summary>Assumed bytes per Siren7 frame (24 kbit/s at 50 frame/s ⇒ 60 bytes).</summary>
  public const int DefaultFrameBytes = 60;

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Siren";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Siren7 / ITU-T G.722.1";
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
public string DefaultExtension => ".g7221";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sir", ".g7221"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // Headerless and magicless: dispatch is extension-only (precedent: raw G.723.1 / LPC-10 / CVSD).
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("siren", "Siren7 / G.722.1")];
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
public string Description => "Raw Siren7 / ITU-T G.722.1 stream; decoded to a mono PCM WAV at 16 kHz.";

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
      new("FULL.g7221", "Container", blob, "siren"),
    };

    var frameBytes = DefaultFrameBytes;
    var frameCount = blob.Length / frameBytes;
    var decoded = false;
    if (frameCount > 0) {
      try {
        var pcm = SirenCodec.Decode(blob, frameBytes);
        if (pcm.Length > 0) {
          var le = ShortsToLePcm(pcm);
          entries.Add(new("MONO.wav", "Channel",
            PcmCodec.ToWavBlob(le, channels: 1, SirenCodec.SampleRate, bitsPerSample: 16, formatCode: 1), "pcm"));
          decoded = true;
        }
      } catch {
        // Decode failed — FULL.g7221 + metadata only.
      }
    }

    var duration = frameCount * (double)SirenCodec.FrameSize / SirenCodec.SampleRate;
    var meta = new StringBuilder();
    meta.AppendLine("; Raw Siren7 / G.722.1 is headerless; frame size is assumed from the default bitrate.");
    meta.AppendLine("codec=Siren7 / ITU-T G.722.1 (MLT, wide-band)");
    meta.Append("sample_rate=").AppendLine(SirenCodec.SampleRate.ToString(CultureInfo.InvariantCulture));
    meta.AppendLine("channels=1");
    meta.Append("regions=").AppendLine(SirenCodec.NumberOfRegions.ToString(CultureInfo.InvariantCulture));
    meta.Append("samples_per_frame=").AppendLine(SirenCodec.FrameSize.ToString(CultureInfo.InvariantCulture));
    meta.Append("assumed_frame_bytes=").AppendLine(frameBytes.ToString(CultureInfo.InvariantCulture));
    meta.Append("frames=").AppendLine(frameCount.ToString(CultureInfo.InvariantCulture));
    meta.Append("duration_seconds=").AppendLine(duration.ToString("0.###", CultureInfo.InvariantCulture));
    meta.Append("decoded=").AppendLine(decoded ? "true" : "false");
    meta.AppendLine("note=Siren7 only; G.722.1 Annex C (Siren14, 32 kHz) is not supported.");
    meta.AppendLine("note=decode-only; G.722.1 has no encoder here.");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
