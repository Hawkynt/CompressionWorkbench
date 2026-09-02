#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.WwiseIma;
using Compression.Registry;

namespace FileFormat.Wem;

/// <summary>
/// Exposes an Audiokinetic Wwise <c>.wem</c> file as a read-only pseudo-archive of
/// <c>FULL.wem</c>, one decoded mono PCM WAV per channel (named per
/// <see cref="ChannelLayout"/>), a <c>metadata.ini</c> (format tag, sample rate, channels),
/// and any auxiliary chunks (<c>akd </c>, <c>cue </c>, …) as <c>metadata/&lt;id&gt;.bin</c>.
/// <para>WEM shares WAV's <c>RIFF…WAVE</c> magic, so this descriptor registers <b>no</b>
/// magic signature (mirroring <c>FlacArchiveDescriptor</c>): WAV keeps the magic and WEM is
/// reached by its <c>.wem</c> extension or explicit registry lookup.</para>
/// <para>Decoding dispatches on the <c>fmt </c> tag: <c>0x0002</c> (Wwise IMA) →
/// <see cref="WwiseImaCodec"/>; <c>0x0001</c>/<c>0xFFFE</c> (PCM) → channel split; everything
/// else (e.g. <c>0xFFFF</c> Wwise Vorbis) surfaces FULL plus a metadata note only.</para>
/// </summary>
public sealed class WemFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  private const int TagPcm = 0x0001;
  private const int TagWwiseIma = 0x0002;
  private const int TagExtensible = 0xFFFE;
  private const int TagWwiseVorbis = 0xFFFF;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Wem";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Wwise Encoded Media";
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
  public string DefaultExtension => ".wem";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".wem"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // Empty — WAV owns the RIFF/WAVE magic; this descriptor is reached by the .wem
  // extension or explicit registry lookup (mirrors FlacArchiveDescriptor's precedent).
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
  public string Description => "Audiokinetic Wwise encoded media (.wem); full file + decoded per-channel WAVs.";

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
      new("FULL.wem", "Container", blob),
    };

    var reader = new WemReader(blob);

    var info = new StringBuilder();
    info.AppendLine($"format_tag=0x{reader.FormatTag:X4}");
    info.AppendLine($"channels={reader.Channels}");
    info.AppendLine($"sample_rate={reader.SampleRate}");
    info.AppendLine($"bits_per_sample={reader.BitsPerSample}");
    info.AppendLine($"block_align={reader.BlockAlign}");
    if (reader.ChannelMask != 0)
      info.AppendLine($"channel_mask=0x{reader.ChannelMask:X8}");

    var channelMask = reader.ChannelMask != 0 ? reader.ChannelMask : (ulong?)null;

    switch (reader.FormatTag) {
      case TagWwiseIma when CanSplitImaBlocks(reader): {
        try {
          var decoded = WwiseImaCodec.Decode(reader.Data, reader.Channels, reader.BlockAlign);
          var pcm = ShortsToLePcm(decoded);
          AddChannels(entries, pcm, reader.Channels, reader.SampleRate, channelMask);
        } catch (Exception ex) when (ex is ArgumentException or InvalidDataException) {
          info.AppendLine($"note=Wwise IMA decode failed ({ex.Message}); FULL only");
        }
        break;
      }
      case TagPcm:
      case TagExtensible when reader.BitsPerSample is 8 or 16 or 24 or 32: {
        AddChannels(entries, reader.Data, reader.Channels, reader.SampleRate, channelMask, reader.BitsPerSample);
        break;
      }
      case TagWwiseVorbis:
        info.AppendLine("note=Wwise Vorbis (tag 0xFFFF) not decodable in this build; FULL only");
        break;
      default:
        info.AppendLine($"note=format tag 0x{reader.FormatTag:X4} not decodable in this build; FULL only");
        break;
    }

    // Auxiliary chunks (akd , cue , …) → metadata/<id>.bin.
    foreach (var (id, data) in reader.ExtraChunks)
      entries.Add(new($"metadata/{SanitizeChunkId(id)}.bin", "Tag", data));

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    return entries;
  }

  private static bool CanSplitImaBlocks(WemReader reader) {
    if (reader.Channels < 1 || reader.BlockAlign < WwiseImaCodec.HeaderBytes * reader.Channels)
      return false;
    var perChannel = reader.BlockAlign / reader.Channels - WwiseImaCodec.HeaderBytes;
    return perChannel >= 0 && perChannel % WwiseImaCodec.GroupBytes == 0;
  }

  private static void AddChannels(List<AudioPseudoArchive.Entry> entries, byte[] pcm,
      int channels, int sampleRate, ulong? channelMask, int bitsPerSample = 16) {
    if (channels < 1) return;
    var bytesPerFrame = channels * bitsPerSample / 8;
    if (bytesPerFrame == 0 || pcm.Length % bytesPerFrame != 0) {
      // Trim a partial trailing frame so the splitter sees whole frames.
      var whole = (pcm.Length / Math.Max(bytesPerFrame, 1)) * Math.Max(bytesPerFrame, 1);
      pcm = pcm[..whole];
    }
    var split = PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, bitsPerSample, channelMask);
    foreach (var (name, wavBlob) in split)
      entries.Add(new($"{name}.wav", "Channel", wavBlob, "wwise"));
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static string SanitizeChunkId(string id) {
    Span<char> chars = stackalloc char[id.Length];
    for (var i = 0; i < id.Length; ++i) {
      var c = id[i];
      chars[i] = char.IsLetterOrDigit(c) ? c : '_';
    }
    return new string(chars).TrimEnd('_');
  }
}
