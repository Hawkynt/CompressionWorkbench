using System.Drawing;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Theming;
using Compression.NativeUI.ViewModels;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Entry details: identity, sizes and ratio, plus byte statistics for a file or a child count for a
/// directory. A liquid-filled isometric box on the left restates the ratio at a glance.
/// </summary>
internal sealed class PropertiesWindow : Form {
  private const int RatioColumnWidth = 60;
  private const int LabelColumn = 130;
  private const int RowHeight = 22;

  private static readonly Color HeadingColor = Color.FromArgb(0x44, 0x66, 0xAA);

  private readonly RatioBarControl _ratioBar = new();
  private readonly Panel _content = new() { AutoScroll = true };

  private readonly PictureBox _entryIcon = new() { SizeMode = PictureBoxSizeMode.Zoom };
  private readonly Label _entryName = new();
  private readonly Label _entryPath = new();

  private readonly Label _originalSize = new();
  private readonly Label _compressedSize = new();
  private readonly Label _ratio = new();
  private readonly Label _savings = new();
  private readonly Label _method = new();
  private readonly Label _modified = new();
  private readonly Label _encrypted = new();
  private readonly Label _type = new();

  private readonly StatisticsControl _stats = new() { Visible = false };
  private readonly List<Control> _folderStats = [];
  private readonly Label _fileCount = new();
  private readonly Label _subdirCount = new();

  private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK };

  public PropertiesWindow() {
    this.Text = "Properties";
    this.ClientSize = new(520, 780);
    this.MinimumSize = new(440, 520);
    this.StartPosition = FormStartPosition.CenterParent;

    this.BuildLayout();
    this.Controls.AddRange(this._ratioBar, this._content);

    this.AcceptButton = this._ok;
    this.CancelButton = this._ok;
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
  }

  private void BuildLayout() {
    var y = 4;

    this._entryIcon.Bounds = new(0, y, 28, 28);
    this._entryName.Bounds = new(38, y, 400, 18);
    this._entryName.Font = new(DefaultTheme.Instance.DefaultFont.Family, 11f, FontStyle.Bold);
    this._entryPath.Bounds = new(38, y + 18, 400, 16);
    this._entryPath.ForeColor = Color.Gray;
    this._entryPath.Font = new(DefaultTheme.Instance.DefaultFont.Family, 8f, FontStyle.Regular);
    this._content.Controls.AddRange(this._entryIcon, this._entryName, this._entryPath);
    y += 44;

    y = this.AddHeading("General", y);
    y = this.AddRow("Original size:", this._originalSize, y);
    y = this.AddRow("Compressed size:", this._compressedSize, y);

    // Ratio and savings share one row, as they did in the WPF layout.
    this._content.Controls.Add(new Label { Bounds = new(8, y, LabelColumn, RowHeight), Text = "Compression ratio:" });
    this._ratio.Bounds = new(8 + LabelColumn, y, 70, RowHeight);
    this._savings.Bounds = new(8 + LabelColumn + 78, y, 250, RowHeight);
    this._savings.ForeColor = Color.Gray;
    this._content.Controls.AddRange(this._ratio, this._savings);
    y += RowHeight;

    y = this.AddRow("Method:", this._method, y);
    y = this.AddRow("Last modified:", this._modified, y);
    y = this.AddRow("Encrypted:", this._encrypted, y);
    y = this.AddRow("Type:", this._type, y) + 10;

    var folderStart = this._content.Controls.Count;
    y = this.AddHeading("Contents", y);
    y = this.AddRow("Files:", this._fileCount, y);
    y = this.AddRow("Subdirectories:", this._subdirCount, y) + 10;
    for (var i = folderStart; i < this._content.Controls.Count; ++i) {
      var control = this._content.Controls[i];
      control.Visible = false;
      this._folderStats.Add(control);
    }

    this._stats.Bounds = new(0, y, 460, 640);
    this._content.Controls.Add(this._stats);
    y += 4;

    this._ok.Bounds = new(370, y, 80, 26);
    this._ok.Click += (_, _) => this.Close();
    this._content.Controls.Add(this._ok);
  }

  private int AddHeading(string text, int y) {
    this._content.Controls.Add(new Label {
      Bounds = new(0, y, 400, 20),
      Text = text,
      ForeColor = HeadingColor,
      Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold),
    });
    return y + 24;
  }

  private int AddRow(string caption, Label value, int y) {
    this._content.Controls.Add(new Label { Bounds = new(8, y, LabelColumn, RowHeight), Text = caption });
    value.Bounds = new(8 + LabelColumn, y, 320, RowHeight);
    this._content.Controls.Add(value);
    return y + RowHeight;
  }

  private void LayoutChildren() {
    this._ratioBar.Bounds = new(4, 12, RatioColumnWidth - 8, Math.Max(0, this.ClientSize.Height - 24));
    this._content.Bounds = new(RatioColumnWidth + 12, 8,
      Math.Max(0, this.ClientSize.Width - RatioColumnWidth - 20),
      Math.Max(0, this.ClientSize.Height - 16));
  }

  /// <summary>Populates the window for <paramref name="entry"/>.</summary>
  public void ShowProperties(ArchiveEntryViewModel entry, IReadOnlyList<ArchiveEntryViewModel> allEntries, byte[]? data) {
    this.Text = $"Properties — {entry.Name}";

    this._entryIcon.Image = Images.Icon(entry.IconKey, 28);
    this._entryName.Text = entry.Name;
    this._entryPath.Text = string.IsNullOrEmpty(entry.Path) ? entry.Name : entry.Path;

    this._originalSize.Text = StatisticsControl.FormatSizeDetailed(entry.OriginalSize);
    this._compressedSize.Text = entry.CompressedSize >= 0
      ? StatisticsControl.FormatSizeDetailed(entry.CompressedSize)
      : "N/A";

    var ratio = entry.OriginalSize > 0 && entry.CompressedSize >= 0
      ? 100.0 * entry.CompressedSize / entry.OriginalSize
      : -1;
    this._ratioBar.Ratio = ratio;

    if (ratio >= 0) {
      this._ratio.Text = $"{ratio:F1}%";
      var savings = entry.OriginalSize - entry.CompressedSize;
      this._savings.Text = savings >= 0
        ? $"(saved {StatisticsControl.FormatSize(savings)})"
        : $"(expanded by {StatisticsControl.FormatSize(-savings)})";
    } else {
      this._ratio.Text = "N/A";
      this._savings.Text = "";
    }

    this._method.Text = string.IsNullOrEmpty(entry.Method) ? "(none)" : entry.Method;
    this._modified.Text = entry.LastModified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";
    this._encrypted.Text = entry.IsEncrypted ? "Yes" : "No";
    this._type.Text = entry.IsDirectory ? "Directory" : GetFileType(entry.Name);

    if (entry.IsDirectory) {
      foreach (var control in this._folderStats) control.Visible = true;

      var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
      var fileCount = 0;
      var subdirs = new HashSet<string>(StringComparer.Ordinal);
      foreach (var e in allEntries) {
        if (!e.Path.StartsWith(dirPrefix, StringComparison.Ordinal)) continue;
        var remainder = e.Path[dirPrefix.Length..];
        if (string.IsNullOrEmpty(remainder)) continue;
        var slash = remainder.IndexOf('/');
        if (slash < 0 && !e.IsDirectory) ++fileCount;
        else if (slash >= 0) subdirs.Add(remainder[..slash]);
      }

      this._fileCount.Text = fileCount.ToString("N0");
      this._subdirCount.Text = subdirs.Count.ToString("N0");
    }

    if (data is { Length: > 0 }) {
      this._stats.Visible = true;
      this._stats.Data = data;
    }
  }

  private static string GetFileType(string name) {
    var ext = Path.GetExtension(name).ToLowerInvariant();
    return ext switch {
      ".txt" or ".log" or ".csv" => "Text file",
      ".xml" or ".html" or ".htm" or ".xaml" => "Markup file",
      ".json" or ".yaml" or ".yml" or ".toml" => "Config file",
      ".cs" or ".java" or ".cpp" or ".c" or ".h" or ".py" or ".js" or ".ts" => "Source code",
      ".exe" or ".dll" or ".sys" => "Executable",
      ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" => "Image",
      ".mp3" or ".wav" or ".flac" or ".ogg" or ".aac" => "Audio",
      ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "Video",
      ".pdf" => "PDF document",
      ".doc" or ".docx" => "Word document",
      ".xls" or ".xlsx" => "Spreadsheet",
      ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
      "" => "File",
      _ => $"{ext.TrimStart('.').ToUpperInvariant()} file",
    };
  }
}
