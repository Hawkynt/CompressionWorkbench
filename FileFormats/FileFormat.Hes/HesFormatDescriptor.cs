#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Hes;

/// <summary>
/// Surfaces a PC Engine (TurboGrafx-16) HES music file (<c>.hes</c>) as a metadata-rich
/// pseudo-archive. HES carries a HuC6280 program plus data that drive the PC Engine's PSG; there
/// is no audio to decode, so the loaded data blocks are surfaced verbatim as Kind <c>Stream</c>
/// blobs.
/// <para>Layout: a 0x10-byte header — magic <c>HESM</c>, u8 version, u8 firstSong, u16 initAddr
/// (request address), and an 8-byte initial MPR (memory-paging) table — followed by one or more
/// data blocks. Each data block begins with its own 0x10-byte block header: a <c>DATA</c> tag,
/// u32 length, u32 loadAddr, then padding to 0x10, after which <c>length</c> bytes of program
/// data follow. Blocks are surfaced as <c>blocks/NN_&lt;hex-loadaddr&gt;.bin</c>; if no
/// <c>DATA</c> block header is found the remainder after the file header is surfaced whole as
/// <c>program.bin</c>.</para>
/// <para>Interpretation note: the HES data-block layout is loosely specified across rippers; we
/// chase contiguous <c>DATA</c> blocks (length + loadAddr from the block header) and fall back to
/// a single <c>program.bin</c> when the structured walk yields nothing.</para>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class HesFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Hes";
  public string DisplayName => "PC Engine HES";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".hes";
  public IReadOnlyList<string> Extensions => [".hes"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("HESM"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PC Engine HES music file; full file + header metadata + HuC6280 data blocks.";

  private const int HeaderSize = 0x10;
  private const int BlockHeaderSize = 0x10;

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
      new("FULL.hes", "Container", blob),
    };

    if (blob.Length < HeaderSize)
      return entries;

    var version = blob[0x04];
    var firstSong = blob[0x05];
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x06));

    var sb = new StringBuilder();
    sb.AppendLine("[hes]");
    sb.AppendLine($"version={version}");
    sb.AppendLine($"first_song={firstSong}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    var mpr = new string[8];
    for (var i = 0; i < 8; ++i)
      mpr[i] = $"0x{blob[0x08 + i]:X2}";
    sb.AppendLine($"initial_mpr={string.Join(' ', mpr)}");

    var blockCount = ExtractDataBlocks(blob, entries);
    if (blockCount == 0 && blob.Length > HeaderSize)
      entries.Add(new("program.bin", "Stream", blob[HeaderSize..]));

    sb.AppendLine($"data_blocks={blockCount}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

    return entries;
  }

  /// <summary>
  /// Walks contiguous <c>DATA</c> blocks starting after the file header. Each block header is
  /// <c>DATA</c> + u32 length + u32 loadAddr; <c>length</c> payload bytes follow at offset 0x10
  /// from the block start.
  /// </summary>
  private static int ExtractDataBlocks(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    var pos = HeaderSize;
    var count = 0;
    while (pos + BlockHeaderSize <= blob.Length) {
      if (!(blob[pos] == 'D' && blob[pos + 1] == 'A' && blob[pos + 2] == 'T' && blob[pos + 3] == 'A'))
        break;

      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 4));
      var loadAddr = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 8));
      var payloadStart = pos + BlockHeaderSize;
      if (length < 0 || payloadStart + length > blob.Length)
        break;

      entries.Add(new($"blocks/{count:D2}_{loadAddr:X4}.bin", "Stream", blob[payloadStart..(payloadStart + length)]));
      ++count;
      pos = payloadStart + length;
    }
    return count;
  }
}
