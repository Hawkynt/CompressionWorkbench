namespace Compression.UI.ViewModels;

/// <summary>
/// View model for a single entry in an opened archive.
/// </summary>
internal sealed class ArchiveEntryViewModel {
  public int Index { get; init; }
  public string Name { get; init; } = "";
  public string Path { get; init; } = "";
  public long OriginalSize { get; init; }
  public long CompressedSize { get; init; }
  public string Method { get; init; } = "";
  public bool IsDirectory { get; init; }
  public bool IsEncrypted { get; init; }
  public bool IsParentEntry { get; init; }
  public DateTime? LastModified { get; init; }

  public string OriginalSizeText => IsParentEntry ? "" : FormatSize(OriginalSize);
  public string CompressedSizeText => IsParentEntry ? "" : (CompressedSize >= 0 ? FormatSize(CompressedSize) : "");
  public string RatioText => IsParentEntry ? "" : (CompressedSize >= 0 && OriginalSize > 0
    ? $"{100.0 * CompressedSize / OriginalSize:F1}%"
    : "");
  public string LastModifiedText => IsParentEntry ? "" : (LastModified?.ToString("yyyy-MM-dd HH:mm") ?? "");
  public string MethodText => IsParentEntry ? "" : Method;

  // Icon glyph (Segoe MDL2 Assets)
  public string Icon => IsParentEntry ? "\uE74A"         // Up arrow
    : IsDirectory ? "\uE8B7"                              // Folder
    : IsEncrypted ? "\uE72E"                              // Lock
    : "\uE8A5";                                           // Document

  // Icon color for visual distinction
  public string IconColor => IsParentEntry ? "#FF4488CC"  // Steel blue
    : IsDirectory ? "#FFDAA520"                            // Goldenrod
    : IsEncrypted ? "#FFCC4444"                            // Red
    : "#FF6699CC";                                         // Slate blue

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };
}
