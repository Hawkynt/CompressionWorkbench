#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Atrac1;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Aea;

/// <summary>
/// Exposes a Sony MD STUDIO / MiniDisc ATRAC1 file (<c>.aea</c>) as a pseudo-archive: the
/// byte-exact original is <c>FULL.aea</c> (Kind <c>Container</c>), every decoded speaker is a mono
/// 44100 Hz PCM <c>&lt;CHANNEL&gt;.wav</c> (Kind <c>Channel</c>) via <c>Codec.Atrac1</c>, and the
/// 2048-byte header's title + channel count become <c>metadata.ini</c> (Kind <c>Tag</c>).
/// <para>The AEA header carries no strong magic — it begins with the little-endian marker
/// <c>00 08 00 00</c> (matching FFmpeg's demuxer probe), a 256-byte title, a block count and the
/// channel count at offset 264. Detection is therefore structural (LE 0x800 marker + channel count
/// 1/2 + the payload being a whole number of 212-byte-per-channel sound units) and extension-based.
/// Read-only; decode failures degrade to the <c>FULL.aea</c> view via try/catch.</para>
/// </summary>
public sealed class AeaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  private const int HeaderSize = 2048;
  private const int SoundUnitSize = 212; // Atrac1Codec.SoundUnitSize
  private const int ChannelOffset = 264;
  private const uint Marker = 0x0000_0800; // little-endian 00 08 00 00
  private const int SampleRate = 44100;

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Aea";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Sony ATRAC1 / MiniDisc (.aea)";
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
public string DefaultExtension => ".aea";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".aea"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // No strong magic; the LE 0x800 marker is only the first four bytes and clashes with anything
  // that happens to start that way, so confidence is deliberately low and detection leans on the
  // extension plus the structural validation in CanHandle.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x08, 0x00, 0x00], Confidence: 0.25),
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
public string Description => "Sony ATRAC1 / MiniDisc (.aea) audio; full file + per-channel PCM + metadata.";

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

  /// <summary>
  /// Structural validation mirroring FFmpeg's <c>aea_read_probe</c>: the four-byte LE marker is
  /// 0x800, the channel count at offset 264 is 1 or 2, and the payload after the 2048-byte header
  /// is a whole number of <c>212 × channels</c>-byte sound units. Exposed so detection / tests can
  /// confirm a file is plausibly AEA without decoding it.
  /// </summary>
  public static bool LooksLikeAea(ReadOnlySpan<byte> b) {
    if (b.Length <= HeaderSize + SoundUnitSize)
      return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(b) != Marker)
      return false;
    int channels = b[ChannelOffset];
    if (channels is not (1 or 2))
      return false;
    var blockSize = channels * SoundUnitSize;
    var payload = b.Length - HeaderSize;
    return payload >= blockSize && payload % blockSize == 0;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.aea", "Container", blob),
    };

    var channels = blob.Length > ChannelOffset ? blob[ChannelOffset] : 0;
    var title = ReadTitle(blob);

    var info = new StringBuilder();
    info.AppendLine("[ATRAC1]");
    info.AppendLine($"title = {title}");
    info.AppendLine($"channels = {channels}");
    info.AppendLine($"sample_rate = {SampleRate}");
    info.AppendLine($"sound_unit_bytes = {SoundUnitSize}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    // Decode the ATRAC1 payload to per-channel WAVs; fall back to the container view on failure.
    if (channels is 1 or 2 && blob.Length > HeaderSize) {
      try {
        var payload = blob.AsSpan(HeaderSize);
        var codec = new Atrac1Codec(channels);
        var interleaved = codec.DecodeStream(payload);
        if (interleaved.Length > 0) {
          var le = new byte[interleaved.Length * 2];
          for (var i = 0; i < interleaved.Length; ++i)
            BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), interleaved[i]);

          if (channels == 1) {
            entries.Add(new("MONO.wav", "Channel",
              PcmCodec.ToWavBlob(le, 1, SampleRate, 16, formatCode: 1), "pcm"));
          } else {
            foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(le, channels, SampleRate, 16))
              entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
          }
        }
      } catch {
        // Undecodable ATRAC1 payload — keep the container view only.
      }
    }

    return entries;
  }

  private static string ReadTitle(byte[] b) {
    if (b.Length < 4 + 256)
      return "";
    var end = 4;
    while (end < 4 + 256 && b[end] != 0)
      ++end;
    return Encoding.Latin1.GetString(b, 4, end - 4);
  }
}
