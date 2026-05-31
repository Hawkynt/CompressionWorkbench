namespace Compression.Registry;

/// <summary>
/// Reusable <see cref="FormatOptionDescriptor"/> building blocks shared by every
/// filesystem that exposes tunable layout parameters through
/// <see cref="IFormatOptionsSchema"/>.
///
/// <para>Cluster/block size and volume size are near-universal across cluster-based
/// filesystems, so they live here rather than being re-declared in each descriptor.
/// Filesystem-specific knobs (MFT record size, inode size, FAT type, …) are declared
/// by the individual descriptor.</para>
/// </summary>
public static class FilesystemSchemaPresets {
  /// <summary>
  /// Standard "Auto + power-of-two" cluster/block size dropdown.
  /// </summary>
  /// <param name="key">The option key (e.g. "ClusterSize", "BlockSize").</param>
  /// <param name="displayName">UI label.</param>
  /// <param name="min">Smallest offered size in bytes (e.g. 512 for FAT, 1024 for ext).</param>
  /// <param name="max">Largest offered size in bytes (e.g. 65536).</param>
  /// <param name="description">Hover help.</param>
  public static FormatOptionDescriptor ClusterSize(
      string key = "ClusterSize",
      string displayName = "Cluster size",
      int min = 512, int max = 65536,
      string? description = null) {
    var values = new List<string> { "Auto" };
    for (var s = min; s <= max; s *= 2)
      values.Add(FormatSize(s));
    return new FormatOptionDescriptor(
      Key: key,
      DisplayName: displayName,
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: values,
      Description: description ??
        "Allocation unit size. Auto picks the size that minimises slack + table overhead.");
  }

  /// <summary>
  /// Standard "Auto (fit to files) + fixed sizes" image-size dropdown.
  /// Pass the medium-specific size labels that the descriptor's parser understands.
  /// </summary>
  public static FormatOptionDescriptor ImageSize(
      IReadOnlyList<string> sizes,
      string? description = null) {
    var values = new List<string> { "Auto (fit to files)" };
    values.AddRange(sizes);
    return new FormatOptionDescriptor(
      Key: "ImageSize",
      DisplayName: "Image size",
      Kind: FormatOptionKind.Enum,
      Default: "Auto (fit to files)",
      AllowedValues: values,
      Description: description ??
        "Total image capacity. Auto sizes the image to exactly hold the files (recommended).");
  }

  /// <summary>Standard volume-label text field.</summary>
  public static FormatOptionDescriptor VolumeLabel(int maxChars = 11) =>
    new(Key: "VolumeLabel",
        DisplayName: "Volume label",
        Kind: FormatOptionKind.String,
        Default: "",
        Description: $"Volume name shown by file managers (max {maxChars} chars).");

  /// <summary>Generic power-of-two size dropdown for any byte-valued knob (inode size, MFT record, …).</summary>
  public static FormatOptionDescriptor PowerOfTwoSize(
      string key, string displayName, int min, int max, string defaultLabel, string description) {
    var values = new List<string> { "Auto" };
    for (var s = min; s <= max; s *= 2)
      values.Add(FormatSize(s));
    return new FormatOptionDescriptor(
      Key: key, DisplayName: displayName, Kind: FormatOptionKind.Enum,
      Default: defaultLabel, AllowedValues: values, Description: description);
  }

  /// <summary>Parses a size label produced by <see cref="FormatSize"/> back into bytes; "Auto"/unknown → 0.</summary>
  public static int ParseSize(string? label) {
    if (string.IsNullOrWhiteSpace(label)) return 0;
    var s = label.Trim();
    if (s.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return 0;
    var parts = s.Split(' ');
    if (parts.Length < 1 || !double.TryParse(parts[0],
        System.Globalization.CultureInfo.InvariantCulture, out var n)) return 0;
    var unit = parts.Length > 1 ? parts[1].ToUpperInvariant() : "B";
    return unit switch {
      "B"  => (int)n,
      "KB" => (int)(n * 1024),
      "MB" => (int)(n * 1024 * 1024),
      _    => (int)n,
    };
  }

  /// <summary>Formats a byte count as "512 B", "4 KB", "1 MB" (powers of two only).</summary>
  public static string FormatSize(long bytes) {
    if (bytes < 1024) return $"{bytes} B";
    if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
    return $"{bytes / (1024 * 1024)} MB";
  }
}
