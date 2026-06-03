#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Codec.Gsm610;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Gsm;

/// <summary>
/// Exposes a raw <c>.gsm</c> file — a bare concatenation of 33-byte GSM 06.10
/// full-rate frames (the "toast"/libgsm on-disk layout), 8000 Hz mono, 160 samples
/// per frame — as an archive of <c>FULL.gsm</c> (byte-exact container),
/// <c>MONO.wav</c> (decoded 16-bit PCM) and <c>metadata.ini</c>.
/// <para>
/// The format is <b>headerless</b>: there is no whole-byte magic. Only the high
/// <i>nibble</i> of each frame's first byte is fixed (<c>0xD</c>, so the byte ranges
/// over <c>0xD0..0xDF</c>) — too weak to register as a <see cref="MagicSignature"/>
/// (it would clash with any payload byte sharing that nibble), so dispatch is by the
/// <c>.gsm</c> extension only and <see cref="MagicSignatures"/> is empty. When the
/// frame stream doesn't structurally validate, the archive gracefully degrades to
/// <c>FULL.gsm</c> + metadata only.
/// </para>
/// <para>
/// <see cref="Codec.Gsm610.Gsm610Codec"/> decodes raw 33-byte frames directly (it is
/// not the WAV49 65-byte double-frame variant), so no extra unpacking is needed; the
/// codec carries no encoder, hence this descriptor is read-only (no
/// <c>IArchiveCreatable</c>).
/// </para>
/// </summary>
public sealed class GsmRawFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  /// <summary>Sample rate of GSM 06.10 full-rate speech.</summary>
  public const int SampleRate = 8000;

  public string Id => "GsmRaw";
  public string DisplayName => "Raw GSM 06.10 (.gsm)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gsm";
  public IReadOnlyList<string> Extensions => [".gsm"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Headerless: only the high nibble (0xD) of each frame's first byte is fixed, so no
  // whole-byte signature exists. Dispatch is by extension only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Raw GSM 06.10 frames; decoded to a mono WAV at 8000 Hz.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.gsm", "Container", blob),
    };

    var decoded = false;
    if (Gsm610Codec.LooksLikeRawFrames(blob)) {
      try {
        var pcm = Gsm610Codec.DecodeRaw(blob);
        var le = ShortsToLePcm(pcm);
        entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(le, channels: 1, SampleRate, bitsPerSample: 16), "pcm"));
        decoded = true;
      } catch {
        // Frames looked structurally valid but didn't decode — FULL.gsm only.
      }
    }

    var frameCount = blob.Length / Gsm610Codec.FrameBytes;
    var info = new StringBuilder();
    info.AppendLine("; Raw GSM 06.10 is headerless; the following are fixed by the format.");
    info.AppendLine("codec=GSM 06.10 full-rate");
    info.Append("sample_rate=").AppendLine(SampleRate.ToString(CultureInfo.InvariantCulture));
    info.AppendLine("channels=1");
    info.Append("frame_bytes=").AppendLine(Gsm610Codec.FrameBytes.ToString(CultureInfo.InvariantCulture));
    info.Append("frames=").AppendLine(frameCount.ToString(CultureInfo.InvariantCulture));
    info.Append("decoded=").AppendLine(decoded ? "true" : "false");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
