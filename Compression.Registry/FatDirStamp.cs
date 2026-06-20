namespace Compression.Registry;

/// <summary>
/// Small helpers for writing FAT directory metadata (creation/modification
/// timestamp and volume label) into the genuine CVF writers' inner FAT volume.
/// Shared so DoubleSpace/DriveSpace 3/Stacker emit identical, spec-correct
/// dir-entry metadata.
/// </summary>
public static class FatDirStamp {
  /// <summary>
  /// Encodes a timestamp into the FAT 16-bit (time, date) dir-entry fields.
  /// Returns <c>(0, 0)</c> for timestamps outside the representable FAT range
  /// (before 1980 or after 2107), which DOS treats as "unset".
  /// </summary>
  public static (ushort Time, ushort Date) Encode(DateTime t) {
    if (t.Year is < 1980 or > 2107) return (0, 0);
    var time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
    var date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
    return (time, date);
  }

  /// <summary>
  /// Parses an ISO-8601 date/time string for a create-option; returns
  /// <c>default(DateTime)</c> (treated as "unset") when blank or unparsable.
  /// </summary>
  public static DateTime Parse(string? iso) =>
    string.IsNullOrWhiteSpace(iso)
      ? default
      : DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
          System.Globalization.DateTimeStyles.None, out var t)
        ? t
        : default;

  /// <summary>
  /// Writes an 11-byte volume-label directory entry (attribute 0x08, no cluster,
  /// zero size) at <paramref name="entryOffset"/>. The label is upper-cased and
  /// space-padded/truncated to 11 bytes, matching the FAT short-name field.
  /// </summary>
  public static void WriteVolumeLabel(byte[] img, int entryOffset, string label) {
    for (var i = 0; i < 11; i++) img[entryOffset + i] = 0x20;
    var bytes = System.Text.Encoding.ASCII.GetBytes(label.ToUpperInvariant());
    System.Array.Copy(bytes, 0, img, entryOffset, System.Math.Min(11, bytes.Length));
    img[entryOffset + 11] = 0x08; // volume-label attribute
  }
}
