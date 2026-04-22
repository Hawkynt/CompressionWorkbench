#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Snes;

/// <summary>
/// SNES / Super Famicom ROM file. No magic bytes — detection is by extension.
/// Surfaces the full ROM, parsed metadata, optional SMC copier header, the ROM body,
/// and the 64-byte internal header region. Read-only.
/// </summary>
public sealed class SnesFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Snes";
  public string DisplayName => "SNES ROM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sfc";
  public IReadOnlyList<string> Extensions => [".sfc", ".smc", ".fig", ".swc"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // SNES ROMs have no reliable magic. Extension-based detection only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Super Nintendo / Super Famicom ROM image (LoROM / HiROM / ExHiROM)";

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

    var entries = new List<(string Name, byte[] Data)> {
      ("FULL.sfc", blob),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; SNES ROM metadata");

    // SMC copier header: 512 bytes if (file size % 1024) == 512.
    var hasSmcHeader = (blob.Length % 1024) == 512 && blob.Length >= 512;
    var bodyOffset = hasSmcHeader ? 512 : 0;
    var bodyLen = blob.Length - bodyOffset;

    if (bodyLen < 0x8000) {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=rom_too_small");
      meta.Append("has_smc_header=").AppendLine(hasSmcHeader ? "true" : "false");
      entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));
      if (hasSmcHeader) {
        var smc = new byte[512];
        Array.Copy(blob, 0, smc, 0, 512);
        entries.Add(("smc_header.bin", smc));
      }
      if (bodyLen > 0) {
        var bodyTiny = new byte[bodyLen];
        Array.Copy(blob, bodyOffset, bodyTiny, 0, bodyLen);
        entries.Add(("rom.bin", bodyTiny));
      }
      return entries;
    }

    // Candidate layouts (offset relative to body, not including SMC header).
    int[] candidates = [0x7FC0, 0xFFC0, 0x40FFC0];
    var bestOffset = -1;
    var bestScore = int.MinValue;
    foreach (var off in candidates) {
      if (off + 0x40 > bodyLen) continue;
      var score = ScoreHeader(blob, bodyOffset + off, bodyLen);
      if (score > bestScore) {
        bestScore = score;
        bestOffset = off;
      }
    }

    if (bestOffset < 0) {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=no_valid_header");
      meta.Append("has_smc_header=").AppendLine(hasSmcHeader ? "true" : "false");
      entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));
      if (hasSmcHeader) {
        var smc = new byte[512];
        Array.Copy(blob, 0, smc, 0, 512);
        entries.Add(("smc_header.bin", smc));
      }
      var body2 = new byte[bodyLen];
      Array.Copy(blob, bodyOffset, body2, 0, bodyLen);
      entries.Add(("rom.bin", body2));
      return entries;
    }

    var headerAbs = bodyOffset + bestOffset;
    // Parse internal header.
    // Title: 21 bytes at +0x00
    var title = Encoding.ASCII.GetString(blob, headerAbs, 21).TrimEnd('\0', ' ');
    var mapMode = blob[headerAbs + 0x15];
    var cartType = blob[headerAbs + 0x16];
    var romSizeExp = blob[headerAbs + 0x17];
    var sramSizeExp = blob[headerAbs + 0x18];
    var region = blob[headerAbs + 0x19];
    var dev = blob[headerAbs + 0x1A];
    var version = blob[headerAbs + 0x1B];
    var checksumCompl = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(headerAbs + 0x1C, 2));
    var checksum = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(headerAbs + 0x1E, 2));
    var checksumValid = (ushort)(checksum ^ checksumCompl) == 0xFFFF;

    var layout = bestOffset switch {
      0x7FC0 => "LoROM",
      0xFFC0 => "HiROM",
      0x40FFC0 => "ExHiROM",
      _ => "Unknown",
    };

    long romSizeKb = romSizeExp <= 20 ? (1L << romSizeExp) : 0;
    long sramSizeKb = sramSizeExp <= 20 ? (sramSizeExp == 0 ? 0 : (1L << sramSizeExp)) / 8 : 0;

    meta.AppendLine("parse_status=ok");
    meta.Append("layout=").AppendLine(layout);
    meta.Append("has_smc_header=").AppendLine(hasSmcHeader ? "true" : "false");
    meta.Append("rom_size_kb=").AppendLine(romSizeKb.ToString(CultureInfo.InvariantCulture));
    meta.Append("sram_size_kb=").AppendLine(sramSizeKb.ToString(CultureInfo.InvariantCulture));
    meta.Append("region=0x").AppendLine(region.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("map_mode=0x").AppendLine(mapMode.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("cart_type=0x").AppendLine(cartType.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("developer_id=0x").AppendLine(dev.ToString("X2", CultureInfo.InvariantCulture));
    meta.Append("version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    meta.Append("checksum_valid=").AppendLine(checksumValid ? "true" : "false");
    meta.Append("title=").AppendLine(title);

    entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));

    if (hasSmcHeader) {
      var smc = new byte[512];
      Array.Copy(blob, 0, smc, 0, 512);
      entries.Add(("smc_header.bin", smc));
    }

    var body = new byte[bodyLen];
    Array.Copy(blob, bodyOffset, body, 0, bodyLen);
    entries.Add(("rom.bin", body));

    // 64-byte internal header region
    var intHdr = new byte[0x40];
    Array.Copy(blob, headerAbs, intHdr, 0, 0x40);
    entries.Add(("internal_header.bin", intHdr));

    return entries;
  }

  private static int ScoreHeader(byte[] blob, int hdrAbs, int bodyLen) {
    if (hdrAbs + 0x40 > blob.Length) return int.MinValue;
    var score = 0;

    // Title printable
    var printable = 0;
    for (var i = 0; i < 21; i++) {
      var b = blob[hdrAbs + i];
      if (b >= 0x20 && b < 0x7F) printable++;
      else if (b == 0x00) printable++; // padding acceptable
    }
    score += printable * 2;

    // Checksum+complement XOR
    var compl = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(hdrAbs + 0x1C, 2));
    var chk = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(hdrAbs + 0x1E, 2));
    if ((ushort)(compl ^ chk) == 0xFFFF) score += 100;

    // Map mode: typical values 0x20..0x35
    var mapMode = blob[hdrAbs + 0x15];
    if (mapMode >= 0x20 && mapMode <= 0x3F) score += 10;

    return score;
  }
}
