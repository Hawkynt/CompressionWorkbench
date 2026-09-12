#pragma warning disable CS1591
using System.Globalization;

namespace FileSystem.Refs;

/// <summary>Stable extent-map names for movable and fixed ReFS structures.</summary>
internal static class RefsMetadataNames {
  private const string PagePrefix = "$ReFS/MSB+/";
  private const string CheckpointPrefix = "$ReFS/CHKP/";

  public const string Vbr = "$ReFS/VBR (fixed)";
  public const string PrimarySuperblock = "$ReFS/SUPB primary (fixed)";
  public const string BackupSuperblock1 = "$ReFS/SUPB backup 1 (fixed)";
  public const string BackupSuperblock2 = "$ReFS/SUPB backup 2 (fixed)";
  public const string TailVbr = "$ReFS/VBR mirror (fixed)";

  public static string Page(ulong physicalHead)
    => PagePrefix + "0x" + physicalHead.ToString("X", CultureInfo.InvariantCulture);

  public static string Checkpoint(ulong physicalHead)
    => CheckpointPrefix + "0x" + physicalHead.ToString("X", CultureInfo.InvariantCulture);

  public static bool TryParsePage(string name, out ulong physicalHead)
    => TryParse(name, PagePrefix, out physicalHead);

  public static bool TryParseCheckpoint(string name, out ulong physicalHead)
    => TryParse(name, CheckpointPrefix, out physicalHead);

  private static bool TryParse(string name, string prefix, out ulong value) {
    value = 0;
    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    var text = name.AsSpan(prefix.Length);
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
    return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
  }
}
