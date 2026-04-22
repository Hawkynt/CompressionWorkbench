#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pst;

/// <summary>
/// Microsoft Outlook personal storage (<c>.pst</c> / <c>.ost</c>). The archive
/// view surfaces: <c>FULL.pst</c> (passthrough), <c>metadata.ini</c>
/// (format=ansi|unicode, version, file size, header CRC, root BBT/NBT offsets)
/// and <c>header.bin</c> (raw 512-byte container header).
/// <para>
/// Scope cut: this descriptor does NOT enumerate the B-tree of node/block pages
/// or extract folders/messages — that's a non-trivial reverse-engineering effort
/// (MS-PST spec, LTP layer, PC/TC tables). Structural surfacing only.
/// </para>
/// </summary>
public sealed class PstFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const int HeaderSize = 512;

  public string Id => "Pst";
  public string DisplayName => "PST / OST (Outlook mailbox)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pst";
  public IReadOnlyList<string> Extensions => [".pst", ".ost"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "!BDN" at offset 0 — 21 42 44 4E.
    new([0x21, 0x42, 0x44, 0x4E], Offset: 0, Confidence: 0.98),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Outlook PST/OST mailbox; header surfacing only (no message enumeration).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.pst", stream.Length, stream.Length, "stored", false, false, null, "Track"),
    };
    foreach (var e in BuildSynthetic(stream))
      entries.Add(new ArchiveEntryInfo(
        entries.Count, e.Name, e.Data.Length, e.Data.Length,
        "stored", false, false, null, e.Kind));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Stream FULL.pst directly — never buffer the whole file.
    if (files == null || files.Length == 0 || MatchesFilter("FULL.pst", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.pst");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }
    foreach (var e in BuildSynthetic(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  // Reads ONLY the first 512 bytes of the stream. Never materializes FULL.pst.
  private static IReadOnlyList<(string Name, byte[] Data, string Kind)> BuildSynthetic(Stream stream) {
    stream.Seek(0, SeekOrigin.Begin);
    var header = new byte[HeaderSize];
    var read = 0;
    while (read < HeaderSize) {
      var n = stream.Read(header, read, HeaderSize - read);
      if (n <= 0) break;
      read += n;
    }
    if (read < HeaderSize) return [];

    var span = header.AsSpan();
    // MS-PST header: magic "!BDN" at offset 0.
    if (span[0] != 0x21 || span[1] != 0x42 || span[2] != 0x44 || span[3] != 0x4E) return [];

    // PST/OST header field layout (common prefix):
    //   [0..4)   dwMagic = "!BDN"
    //   [4..8)   dwCRCPartial
    //   [8..10)  wMagicClient
    //   [10..12) wVer        — 0x0E/0x0F = ANSI (32-bit), 0x15/0x17 = Unicode (64-bit)
    //   [12..14) wVerClient
    // Unicode: root struct at offset 180 (u64 fields).
    // ANSI: root struct at offset 172 (u32 fields).
    var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
    var wVer = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
    var wVerClient = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
    var isUnicode = wVer >= 0x15;

    ulong rootNbt = 0, rootBbt = 0;
    if (isUnicode) {
      const int rootOff = 180;
      if (HeaderSize >= rootOff + 32) {
        rootBbt = BinaryPrimitives.ReadUInt64LittleEndian(span[(rootOff + 16)..]);
        rootNbt = BinaryPrimitives.ReadUInt64LittleEndian(span[(rootOff + 24)..]);
      }
    } else {
      const int rootOff = 172;
      if (HeaderSize >= rootOff + 24) {
        rootBbt = BinaryPrimitives.ReadUInt32LittleEndian(span[(rootOff + 16)..]);
        rootNbt = BinaryPrimitives.ReadUInt32LittleEndian(span[(rootOff + 20)..]);
      }
    }

    var fileSize = stream.Length;
    var ini = new StringBuilder();
    ini.AppendLine("; Outlook PST/OST header");
    ini.Append("format=").AppendLine(isUnicode ? "unicode" : "ansi");
    ini.Append("version=").AppendLine(wVer.ToString(CultureInfo.InvariantCulture));
    ini.Append("version_client=").AppendLine(wVerClient.ToString(CultureInfo.InvariantCulture));
    ini.Append("file_size=").AppendLine(fileSize.ToString(CultureInfo.InvariantCulture));
    ini.Append("header_crc=0x").AppendLine(crc.ToString("X8", CultureInfo.InvariantCulture));
    ini.Append("root_nbt_offset=").AppendLine(rootNbt.ToString(CultureInfo.InvariantCulture));
    ini.Append("root_bbt_offset=").AppendLine(rootBbt.ToString(CultureInfo.InvariantCulture));

    return [
      ("metadata.ini", Encoding.UTF8.GetBytes(ini.ToString()), "Tag"),
      ("header.bin", header, "Track"),
    ];
  }
}
