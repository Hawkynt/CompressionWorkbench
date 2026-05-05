#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Shared entry-building and metadata-formatting helpers for the three
/// firmware-hex descriptors (IntelHex / SRecord / TiTxt).
/// </summary>
internal static class FirmwareHexCommon {

  /// <summary>Builds the standard entry list: <c>metadata.ini</c> + <c>firmware.bin</c>.</summary>
  public static List<(string Name, byte[] Data, string Method)> BuildEntries(FirmwareImage image) =>
  [
    ("metadata.ini", BuildMetadata(image), "stored"),
    ("firmware.bin", image.ToFlatBinary(), "stored"),
  ];

  /// <summary>Wraps the raw entry list in <see cref="ArchiveEntryInfo"/> records.</summary>
  public static List<ArchiveEntryInfo> BuildArchiveEntries(
      List<(string Name, byte[] Data, string Method)> entries) =>
    entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

  private static byte[] BuildMetadata(FirmwareImage image) {
    var sb = new StringBuilder();
    sb.AppendLine("[firmware_hex]");
    sb.Append(CultureInfo.InvariantCulture, $"source_format = {image.SourceFormat}\n");
    sb.Append(CultureInfo.InvariantCulture, $"record_count = {image.RecordCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"segment_count = {image.Segments.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"gap_count = {image.GapCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_data_bytes = {image.TotalDataBytes}\n");
    sb.Append(CultureInfo.InvariantCulture, $"base_address = 0x{image.BaseAddress:X8}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"start_address = {(image.StartAddress.HasValue ? $"0x{image.StartAddress.Value:X8}" : "(unspecified)")}\n");
    for (var i = 0; i < image.Segments.Count; i++) {
      var (a, d) = image.Segments[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"segment_{i} = 0x{a:X8} .. 0x{a + (uint)d.Length:X8} ({d.Length} bytes)\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
