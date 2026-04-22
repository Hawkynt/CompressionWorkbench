#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nes;

/// <summary>
/// iNES / NES 2.0 ROM file. Surfaces the full ROM, parsed metadata, and the PRG/CHR
/// ROM banks as separate files. Read-only.
/// </summary>
public sealed class NesFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nes";
  public string DisplayName => "NES ROM (iNES / NES 2.0)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nes";
  public IReadOnlyList<string> Extensions => [".nes"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // iNES magic: "NES\x1A"
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x4E, 0x45, 0x53, 0x1A], Offset: 0, Confidence: 0.98)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nintendo Entertainment System ROM image (iNES / NES 2.0)";

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
      ("FULL.nes", blob),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; iNES / NES 2.0 ROM metadata");

    if (blob.Length < 16 ||
        blob[0] != 0x4E || blob[1] != 0x45 || blob[2] != 0x53 || blob[3] != 0x1A) {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=missing_ines_magic");
      entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));
      return entries;
    }

    var prg16k = blob[4];
    var chr8k = blob[5];
    var flags6 = blob[6];
    var flags7 = blob[7];
    var flags8 = blob[8];
    var flags9 = blob[9];
    var flags10 = blob[10];

    var hasTrainer = (flags6 & 0x04) != 0;
    var hasBatteryRam = (flags6 & 0x02) != 0;
    var mirroring = (flags6 & 0x08) != 0 ? "four-screen"
                  : (flags6 & 0x01) != 0 ? "vertical"
                  : "horizontal";
    var mapperLo = (flags6 >> 4) & 0x0F;
    var mapperHi = flags7 & 0xF0;
    var mapper = mapperHi | mapperLo;

    // NES 2.0 detection: flags7 bits 2..3 == 0b10
    var isNes2 = (flags7 & 0x0C) == 0x08;
    string? regionIfNes2 = null;
    if (isNes2) {
      // Upper mapper nibble in flags8 low nibble.
      var mapperExt = flags8 & 0x0F;
      mapper |= mapperExt << 8;
      var region = flags10 & 0x03;
      regionIfNes2 = region switch {
        0 => "NTSC",
        1 => "PAL",
        2 => "Multi",
        3 => "Dendy",
        _ => "unknown",
      };
    }

    var prgOffset = 16 + (hasTrainer ? 512 : 0);
    // NES 2.0 size extensions
    long prgSize;
    long chrSize;
    if (isNes2) {
      var prgMsb = (flags9 & 0x0F);
      var chrMsb = (flags9 >> 4) & 0x0F;
      prgSize = ((long)prgMsb << 8 | prg16k) * 16384L;
      chrSize = ((long)chrMsb << 8 | chr8k) * 8192L;
    } else {
      prgSize = (long)prg16k * 16384;
      chrSize = (long)chr8k * 8192;
    }

    meta.Append("parse_status=").AppendLine("ok");
    meta.Append("prg_16kb_banks=").AppendLine(prg16k.ToString(CultureInfo.InvariantCulture));
    meta.Append("chr_8kb_banks=").AppendLine(chr8k.ToString(CultureInfo.InvariantCulture));
    meta.Append("mapper=").AppendLine(mapper.ToString(CultureInfo.InvariantCulture));
    meta.Append("mirroring=").AppendLine(mirroring);
    meta.Append("has_trainer=").AppendLine(hasTrainer ? "true" : "false");
    meta.Append("has_battery_ram=").AppendLine(hasBatteryRam ? "true" : "false");
    meta.Append("nes2_0=").AppendLine(isNes2 ? "true" : "false");
    if (regionIfNes2 != null)
      meta.Append("region_if_nes2=").AppendLine(regionIfNes2);

    entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));

    // Trainer
    if (hasTrainer && blob.Length >= 16 + 512) {
      var trainer = new byte[512];
      Array.Copy(blob, 16, trainer, 0, 512);
      entries.Add(("trainer.bin", trainer));
    }

    // PRG ROM
    if (prgSize > 0 && prgOffset < blob.Length) {
      var available = Math.Min(prgSize, blob.Length - prgOffset);
      if (available > 0) {
        var prg = new byte[available];
        Array.Copy(blob, prgOffset, prg, 0, (int)available);
        entries.Add(("prg_rom.bin", prg));
      }
    }

    // CHR ROM
    if (chrSize > 0) {
      var chrOffset = prgOffset + (int)prgSize;
      if (chrOffset < blob.Length) {
        var available = Math.Min(chrSize, blob.Length - chrOffset);
        if (available > 0) {
          var chr = new byte[available];
          Array.Copy(blob, chrOffset, chr, 0, (int)available);
          entries.Add(("chr_rom.bin", chr));
        }
      }
    }

    return entries;
  }
}
