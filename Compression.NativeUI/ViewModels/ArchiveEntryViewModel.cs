using Compression.NativeUI.Theming;

namespace Compression.NativeUI.ViewModels;

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

  public string OriginalSizeText => this.IsParentEntry ? "" : FormatSize(this.OriginalSize);
  public string CompressedSizeText => this.IsParentEntry ? "" : (this.CompressedSize >= 0 ? FormatSize(this.CompressedSize) : "");
  public string RatioText => this.IsParentEntry ? "" : (this.CompressedSize >= 0 && this.OriginalSize > 0
    ? $"{100.0 * this.CompressedSize / this.OriginalSize:F1}%"
    : "");
  public string LastModifiedText => this.IsParentEntry ? "" : (this.LastModified?.ToString("yyyy-MM-dd HH:mm") ?? "");
  public string MethodText => this.IsParentEntry ? "" : this.Method;

  /// <summary>
  /// Key into the shell's icon <see cref="Hawkynt.NativeForms.ImageList"/>. WPF resolved a vector
  /// <c>ImageSource</c> from the application resource dictionary here; NativeForms addresses list
  /// images by key, so the view model names the icon and the view owns the pixels.
  /// </summary>
  public string IconKey => this.IsParentEntry ? IconKeys.UpArrow
    : this.IsDirectory ? IconKeys.Folder
    : this.IsEncrypted ? IconKeys.LockedFile
    : IconKeys.File;

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };
}
