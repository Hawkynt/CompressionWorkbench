#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Kss;

/// <summary>
/// Surfaces a KSS music file (<c>.kss</c>) as a metadata-rich pseudo-archive. KSS carries a Z80
/// program plus sound-chip data for MSX/SG-1000/Master System hardware; there is no audio to
/// decode, so the data image is surfaced verbatim as a Kind <c>Stream</c> blob.
/// <para>Two magic variants exist. The classic <c>KSCC</c> header is 0x10 bytes: u16 loadAddr,
/// u16 dataLen, u16 initAddr, u16 playAddr, u8 startBank, u8 extraBanks, u8 reserved/extraHeader,
/// u8 deviceFlags. The <c>KSSX</c> variant uses the same 0x10-byte core but the
/// <c>reserved/extraHeader</c> byte (offset 0x0E) declares an extra header length; when it is
/// 0x10 a second 0x10-byte block follows at 0x10 carrying chip-extra flags and the
/// firstSong/songCount words. The Z80 data begins immediately after the (combined) header. The
/// data is surfaced as <c>program.bin</c>.</para>
/// <para>Interpretation note (KSSX extension is sparsely documented): we treat offset 0x0E as
/// the extra-header length. When it is non-zero and the bytes are present, the extension block
/// is parsed for the deviceFlags-extra byte plus the u16 firstSong / u16 songCount words and the
/// payload offset advances past it; otherwise we fall back to the plain 0x10-byte layout.</para>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class KssFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Kss";
  public string DisplayName => "KSS (MSX/SMS music)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".kss";
  public IReadOnlyList<string> Extensions => [".kss"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("KSCC"u8.ToArray(), Confidence: 0.95),
    new("KSSX"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "KSS music file (KSCC/KSSX); full file + header metadata + Z80 data image.";

  private const int CoreHeaderSize = 0x10;

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
      new("FULL.kss", "Container", blob),
    };

    if (blob.Length < CoreHeaderSize)
      return entries;

    var isKssx = blob[0] == 'K' && blob[1] == 'S' && blob[2] == 'S' && blob[3] == 'X';

    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x04));
    var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x06));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x08));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0A));
    var startBank = blob[0x0C];
    var extraBanks = blob[0x0D];
    var extraHeaderLen = blob[0x0E];
    var deviceFlags = blob[0x0F];

    var sb = new StringBuilder();
    sb.AppendLine("[kss]");
    sb.AppendLine($"variant={(isKssx ? "KSSX" : "KSCC")}");
    sb.AppendLine($"load_addr=0x{loadAddr:X4}");
    sb.AppendLine($"data_len=0x{dataLen:X4}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    sb.AppendLine($"play_addr=0x{playAddr:X4}");
    sb.AppendLine($"start_bank=0x{startBank:X2}");
    sb.AppendLine($"extra_banks=0x{extraBanks:X2}");
    sb.AppendLine($"extra_header_len=0x{extraHeaderLen:X2}");
    sb.AppendLine($"device_flags=0x{deviceFlags:X2}");
    sb.AppendLine($"devices={DescribeDevices(deviceFlags)}");

    var payloadOffset = CoreHeaderSize;

    // KSSX extension block: present when offset 0x0E declares a non-zero extra-header length and
    // the bytes are actually present in the file.
    if (isKssx && extraHeaderLen > 0 && blob.Length >= CoreHeaderSize + extraHeaderLen) {
      var ext = blob.AsSpan(CoreHeaderSize, extraHeaderLen);
      if (ext.Length >= 1)
        sb.AppendLine($"extra_device_flags=0x{ext[0]:X2}");
      if (ext.Length >= 3)
        sb.AppendLine($"first_song={BinaryPrimitives.ReadUInt16LittleEndian(ext[1..])}");
      if (ext.Length >= 5)
        sb.AppendLine($"song_count={BinaryPrimitives.ReadUInt16LittleEndian(ext[3..])}");
      payloadOffset = CoreHeaderSize + extraHeaderLen;
    }

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

    if (blob.Length > payloadOffset)
      entries.Add(new("program.bin", "Stream", blob[payloadOffset..]));

    return entries;
  }

  /// <summary>Decodes the KSS device-flags byte into the enabled sound chips.</summary>
  private static string DescribeDevices(byte flags) {
    if (flags == 0)
      return "PSG only";
    var devices = new List<string> { "PSG" };
    if ((flags & 0x01) != 0) devices.Add("FMPAC");
    if ((flags & 0x02) != 0) devices.Add("SCC");
    if ((flags & 0x04) != 0) devices.Add("MSX-MUSIC (FM)");
    if ((flags & 0x08) != 0) devices.Add("MSX-AUDIO");
    return string.Join(", ", devices);
  }
}
