using System.Windows;
using System.Windows.Media;

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

  // Vector icon from resource dictionary
  public ImageSource? IconImage {
    get {
      var key = IsParentEntry ? "UpArrowIcon"
        : IsDirectory ? "FolderIcon"
        : IsEncrypted ? "LockedFileIcon"
        : "FileIcon";
      return Application.Current?.TryFindResource(key) as ImageSource;
    }
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };
}
