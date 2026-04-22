#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gb;

/// <summary>
/// Game Boy / Game Boy Color ROM. Detected via the fixed Nintendo logo at
/// offset 0x0104 (48 bytes). Surfaces the full ROM, parsed metadata, the 80-byte
/// header, and the ROM split into 16 KiB banks. Read-only.
/// </summary>
public sealed class GbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Gb";
  public string DisplayName => "Game Boy / GBC ROM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gb";
  public IReadOnlyList<string> Extensions => [".gb", ".gbc"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Nintendo logo at offset 0x0104 — first 16 bytes serve as our magic.
  // CE ED 66 66 CC 0D 00 0B 03 73 00 83 00 0C 00 0D
  public static readonly byte[] NintendoLogoPrefix = [
    0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B,
    0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
  ];

  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(NintendoLogoPrefix, Offset: 0x0104, Confidence: 0.95)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nintendo Game Boy / Game Boy Color ROM image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IReadOnlyList<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var meta = new StringBuilder();
    meta.AppendLine("; Game Boy / GBC ROM metadata");

    if (blob.Length < 0x0150) {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=rom_too_small");
      return [
        ("FULL.gb", blob),
        ("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())),
      ];
    }

    // Title: 16 bytes at 0x0134; last byte is CGB flag for newer carts.
    var titleBytes = new byte[16];
    Array.Copy(blob, 0x0134, titleBytes, 0, 16);
    var title = Encoding.ASCII.GetString(titleBytes).TrimEnd('\0', ' ');
    // Strip any control chars at tail.
    while (title.Length > 0 && title[^1] < 0x20) title = title[..^1];

    var cgbFlag = blob[0x0143];
    var newLic = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x0144, 2));
    var sgbFlag = blob[0x0146];
    var cartType = blob[0x0147];
    var romSizeCode = blob[0x0148];
    var ramSizeCode = blob[0x0149];
    var destCode = blob[0x014A];
    var oldLic = blob[0x014B];
    var version = blob[0x014C];
    var headerChecksum = blob[0x014D];
    var globalChecksum = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(0x014E, 2));

    // Header checksum: x = 0; for i = 0x134..0x14C: x = x - rom[i] - 1
    byte hc = 0;
    for (var i = 0x0134; i <= 0x014C; i++) hc = (byte)(hc - blob[i] - 1);
    var hcValid = hc == headerChecksum;

    var romBankCount = romSizeCode <= 8 ? 2 << romSizeCode : 0; // 2^(N+1) banks
    var isCgb = cgbFlag is 0x80 or 0xC0;
    var defaultExt = isCgb ? ".gbc" : ".gb";

    meta.AppendLine("parse_status=ok");
    meta.Append("title=").AppendLine(title);
    meta.Append("cgb_flag=0x").AppendLine(cgbFlag.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("sgb_flag=0x").AppendLine(sgbFlag.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("cart_type=0x").AppendLine(cartType.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("rom_size_code=0x").AppendLine(romSizeCode.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("rom_size_banks=").AppendLine(romBankCount.ToString(CultureInfo.InvariantCulture));
    meta.Append("ram_size_code=0x").AppendLine(ramSizeCode.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("region=").AppendLine(destCode == 0 ? "Japanese" : "Non-Japanese");
    meta.Append("old_licensee=0x").AppendLine(oldLic.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("new_licensee=0x").AppendLine(newLic.ToString("X4", CultureInfo.InvariantCulture));
    meta.Append("version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    meta.Append("header_checksum_ok=").AppendLine(hcValid ? "true" : "false");
    meta.Append("global_checksum=0x").AppendLine(globalChecksum.ToString("X4", CultureInfo.InvariantCulture));

    var entries = new List<(string Name, byte[] Data)> {
      ("FULL" + defaultExt, blob),
      ("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())),
    };

    // 80-byte ROM header from 0x0100..0x014F
    var hdr = new byte[0x50];
    Array.Copy(blob, 0x0100, hdr, 0, 0x50);
    entries.Add(("header.bin", hdr));

    // ROM banks — 16 KiB each.
    const int bankSize = 16384;
    var totalBanks = (blob.Length + bankSize - 1) / bankSize;
    for (var b = 0; b < totalBanks; b++) {
      var off = b * bankSize;
      var len = Math.Min(bankSize, blob.Length - off);
      if (len <= 0) break;
      var bank = new byte[len];
      Array.Copy(blob, off, bank, 0, len);
      entries.Add(($"rom_banks/bank_{b:D3}.bin", bank));
    }

    return entries;
  }
}
