#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Codec.Sbc;
using Compression.Registry;

namespace FileFormat.Sbc;

/// <summary>
/// Raw Bluetooth SBC / mSBC container surfaced as a pseudo-archive. An SBC file is a bare
/// concatenation of self-describing frames, each starting with a syncword — <c>0x9C</c> for
/// ordinary A2DP SBC, <c>0xAD</c> for mSBC (wide-band speech). There is no file-level header and
/// no embedded sample-rate beyond what each frame carries.
/// <para>
/// Detection: a single syncword byte is far too weak to register as a confident magic, so the
/// descriptor declares <c>0x9C</c> at offset 0 with <b>low confidence</b> and the registry's
/// structural validation confirms it by walking several consecutive valid frame headers
/// (<see cref="SbcCodec.ReadFrames"/>). The <c>.sbc</c> / <c>.msbc</c> extensions provide the
/// primary, reliable dispatch.
/// </para>
/// <para>
/// The archive view surfaces <c>FULL.sbc</c> (the byte-exact stream, Kind <c>Container</c>), one
/// decoded mono PCM WAV per channel (Kind <c>Channel</c>, named per the speaker layout) and
/// <c>metadata.ini</c> (Kind <c>Tag</c>) describing the first frame's parameters and the frame
/// count. The format is <b>read-only</b>: there is no SBC encoder here.
/// </para>
/// </summary>
public sealed class SbcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Sbc";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Bluetooth SBC / mSBC";
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
public string DefaultExtension => ".sbc";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sbc", ".msbc"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];

  // A single syncword byte (0x9C) is a weak signal; the registry's structural validation
  // (consecutive valid frame headers) confirms it. mSBC's 0xAD is reached via the extension.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new MagicSignature([SbcCodec.SbcSyncword], Offset: 0, Confidence: 0.10)];

    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("sbc", "Bluetooth SBC")];
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
public string Description => "Raw Bluetooth SBC/mSBC frame stream; decoded to per-channel PCM WAVs.";

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
      new("FULL.sbc", "Container", blob, "sbc"),
    };

    var frames = SbcCodec.ReadFrames(blob);
    var first = frames.Count > 0 ? frames[0] : (SbcCodec.FrameHeader?)null;

    var decoded = false;
    if (first is { } header) {
      try {
        var channels = SbcCodec.DecodeToChannels(blob, out var sampleRate, out var channelCount);
        if (channels.Length > 0 && channels[0].Length > 0) {
          var names = PcmCodec.LayoutNames(channelCount);
          for (var ch = 0; ch < channelCount; ++ch) {
            var le = ShortsToLePcm(channels[ch]);
            var wav = PcmCodec.ToWavBlob(le, channels: 1, sampleRate, bitsPerSample: 16, formatCode: 1);
            entries.Add(new($"{names[ch]}.wav", "Channel", wav, "pcm"));
          }
          decoded = true;
        }
      } catch {
        // Frames parsed but synthesis failed — fall back to FULL.sbc + metadata only.
      }
    }

    entries.Add(new("metadata.ini", "Tag", BuildMetadata(first, frames.Count, decoded)));
    return entries;
  }

  private static byte[] BuildMetadata(SbcCodec.FrameHeader? first, int frameCount, bool decoded) {
    var meta = new StringBuilder();
    meta.AppendLine("; Raw SBC/mSBC is headerless; parameters below are read from the first frame.");
    meta.AppendLine("codec=Bluetooth SBC (low-complexity subband codec)");
    if (first is { } h) {
      meta.Append("variant=").AppendLine(h.IsMsbc ? "mSBC" : "SBC");
      meta.Append("sample_rate=").AppendLine(h.SampleRate.ToString(CultureInfo.InvariantCulture));
      meta.Append("channels=").AppendLine(h.Channels.ToString(CultureInfo.InvariantCulture));
      meta.Append("channel_mode=").AppendLine(h.Mode.ToString());
      meta.Append("allocation=").AppendLine(h.AllocationMethod.ToString());
      meta.Append("subbands=").AppendLine(h.Subbands.ToString(CultureInfo.InvariantCulture));
      meta.Append("blocks=").AppendLine(h.Blocks.ToString(CultureInfo.InvariantCulture));
      meta.Append("bitpool=").AppendLine(h.Bitpool.ToString(CultureInfo.InvariantCulture));
      meta.Append("samples_per_frame=")
        .AppendLine((h.Blocks * h.Subbands).ToString(CultureInfo.InvariantCulture));
    }
    meta.Append("frames=").AppendLine(frameCount.ToString(CultureInfo.InvariantCulture));
    meta.Append("decoded=").AppendLine(decoded ? "true" : "false");
    meta.AppendLine("note=decode-only; SBC has no encoder here.");
    return Encoding.UTF8.GetBytes(meta.ToString());
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
