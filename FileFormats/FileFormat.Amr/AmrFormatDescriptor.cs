#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.AmrNb;
using Codec.AmrWb;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Amr;

/// <summary>
/// 3GPP AMR storage container (the <c>.amr</c> / <c>.awb</c> file format, RFC 4867 storage mode).
/// The leading magic selects the variant:
/// <list type="bullet">
///   <item><c>#!AMR\n</c> — AMR narrowband, mono 8 kHz.</item>
///   <item><c>#!AMR-WB\n</c> — AMR wideband, mono 16 kHz.</item>
///   <item><c>#!AMR_MC1.0\n</c> + a 4-byte little-endian channel count — multi-channel NB.</item>
///   <item><c>#!AMR-WB_MC1.0\n</c> + a 4-byte channel count — multi-channel WB.</item>
/// </list>
/// After the header the body is a sequence of frames; each frame's first byte carries the mode in
/// bits 3..6, which sizes the frame (NB payload bytes {12,13,15,17,19,20,26,31}; WB
/// {17,23,32,36,40,46,50,58,60}). For a multi-channel file the per-channel frames are interleaved
/// frame-by-frame, matching the ffmpeg AMR demuxer.
/// <para>The archive view surfaces <c>FULL.amr</c>/<c>FULL.awb</c> (byte-exact stream, Kind
/// <c>Container</c>), a decoded <c>MONO.wav</c> (or one WAV per channel for MC files, Kind
/// <c>Channel</c>) and <c>metadata.ini</c> (Kind <c>Tag</c>). Read-only: AMR has no encoder here.</para>
/// </summary>
public sealed class AmrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  private static readonly byte[] MagicNb = "#!AMR\n"u8.ToArray();
  private static readonly byte[] MagicWb = "#!AMR-WB\n"u8.ToArray();
  private static readonly byte[] MagicNbMc = "#!AMR_MC1.0\n"u8.ToArray();
  private static readonly byte[] MagicWbMc = "#!AMR-WB_MC1.0\n"u8.ToArray();

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Amr";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "3GPP AMR";
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
public string DefaultExtension => ".amr";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".amr", ".awb", ".3ga"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // The single "#!AMR" prefix is shared by all four variants (NB / WB / MC); the descriptor
  // resolves the exact variant from the full header during parsing.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("#!AMR"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("amr", "3GPP AMR")];
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
public string Description => "3GPP AMR narrowband/wideband speech container; full file + decoded PCM WAV(s).";

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

  private enum Variant { Nb, Wb, NbMc, WbMc, Unknown }

  private static (Variant Variant, int HeaderLen, int Channels) ParseHeader(byte[] blob) {
    // Order matters: the longer MC magics must be checked before the short NB magic.
    if (StartsWith(blob, MagicWbMc))
      return (Variant.WbMc, MagicWbMc.Length + 4, ReadChannels(blob, MagicWbMc.Length));
    if (StartsWith(blob, MagicNbMc))
      return (Variant.NbMc, MagicNbMc.Length + 4, ReadChannels(blob, MagicNbMc.Length));
    if (StartsWith(blob, MagicWb))
      return (Variant.Wb, MagicWb.Length, 1);
    if (StartsWith(blob, MagicNb))
      return (Variant.Nb, MagicNb.Length, 1);
    return (Variant.Unknown, 0, 0);
  }

  private static int ReadChannels(byte[] blob, int offset) =>
    offset + 4 <= blob.Length ? BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(offset, 4)) : 1;

  private static bool StartsWith(byte[] blob, byte[] magic) {
    if (blob.Length < magic.Length)
      return false;
    for (var i = 0; i < magic.Length; i++)
      if (blob[i] != magic[i])
        return false;
    return true;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var (variant, headerLen, channels) = ParseHeader(blob);
    var isWb = variant is Variant.Wb or Variant.WbMc;
    var isMc = variant is Variant.NbMc or Variant.WbMc;
    var fullExt = isWb ? "awb" : "amr";
    var fullName = $"FULL.{fullExt}";

    var entries = new List<AudioPseudoArchive.Entry> {
      new(fullName, "Container", blob, "amr"),
    };

    if (variant == Variant.Unknown) {
      var unknownMeta = new StringBuilder();
      unknownMeta.AppendLine("codec=3GPP AMR");
      unknownMeta.AppendLine("note=unrecognized header; expected #!AMR / #!AMR-WB / #!AMR_MC1.0 / #!AMR-WB_MC1.0.");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(unknownMeta.ToString())));
      return entries;
    }

    if (isMc && channels < 1)
      channels = 1;
    var channelCount = isMc ? channels : 1;

    var sampleRate = isWb ? AmrWbCodec.SampleRate : AmrNbCodec.SampleRate;
    var samplesPerFrame = isWb ? AmrWbCodec.SamplesPerFrame : AmrNbCodec.SamplesPerFrame;

    var body = blob.AsSpan(headerLen);

    // Split the interleaved frame stream into one byte stream per channel.
    var channelStreams = SplitChannels(body, channelCount, isWb);

    var totalFrames = 0;
    var names = channelCount > 1 ? PcmCodec.LayoutNames(channelCount) : null;
    for (var ch = 0; ch < channelCount; ch++) {
      var chBytes = channelStreams[ch];
      short[] linear = isWb ? AmrWbCodec.Decode(chBytes) : AmrNbCodec.Decode(chBytes);
      var frames = isWb ? AmrWbCodec.CountFrames(chBytes) : AmrNbCodec.CountFrames(chBytes);
      totalFrames += frames;

      var wavBlob = PcmCodec.ToWavBlob(ShortsToLePcm(linear), channels: 1, sampleRate, bitsPerSample: 16, formatCode: 1);
      var name = channelCount > 1 ? $"{names![ch]}.wav" : "MONO.wav";
      entries.Add(new(name, "Channel", wavBlob, "pcm"));
    }

    var durationSeconds = channelCount > 0
      ? (channelStreams[0].Length > 0
          ? (isWb ? AmrWbCodec.CountFrames(channelStreams[0]) : AmrNbCodec.CountFrames(channelStreams[0]))
            * (double)samplesPerFrame / sampleRate
          : 0.0)
      : 0.0;

    var meta = new StringBuilder();
    meta.AppendLine($"codec=AMR-{(isWb ? "WB (G.722.2 / 3GPP TS 26.190)" : "NB (3GPP TS 26.090)")}");
    meta.AppendLine($"variant={variant}");
    meta.AppendLine($"channels={channelCount}");
    meta.AppendLine($"sample_rate={sampleRate}");
    meta.AppendLine($"frame_samples={samplesPerFrame}");
    meta.AppendLine($"frames_total={totalFrames}");
    meta.AppendLine($"duration_seconds={durationSeconds:0.###}");
    meta.AppendLine("note=decode-only; AMR has no encoder.");
    meta.AppendLine("note=SID/NO_DATA frames render as silence (DTX comfort noise not synthesized).");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }

  // Walk the interleaved frame body, distributing each successive frame to channels in round-robin
  // order (the AMR_MC storage layout). For a single channel this is just the whole body.
  private static byte[][] SplitChannels(ReadOnlySpan<byte> body, int channelCount, bool isWb) {
    var builders = new List<byte>[channelCount];
    for (var i = 0; i < channelCount; i++)
      builders[i] = [];

    var pos = 0;
    var ch = 0;
    while (pos < body.Length) {
      var frameType = (body[pos] >> 3) & 0x0F;
      var size = isWb ? AmrWbCodec.FrameBytes(frameType) : 1 + AmrNbCodec.PayloadBytes(frameType);
      if (size <= 0)
        size = 1;
      if (pos + size > body.Length)
        break;
      for (var i = 0; i < size; i++)
        builders[ch].Add(body[pos + i]);
      pos += size;
      ch = (ch + 1) % channelCount;
    }

    var result = new byte[channelCount][];
    for (var i = 0; i < channelCount; i++)
      result[i] = builders[i].ToArray();
    return result;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; i++)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
