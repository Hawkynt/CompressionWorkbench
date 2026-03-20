using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Compression.UI.Views;

public partial class PreviewWindow : Window {
  private byte[] _data = [];
  private bool _hexMode;

  public PreviewWindow() {
    InitializeComponent();
  }

  public void ShowData(string entryName, byte[] data, bool hex = false) {
    _data = data;
    _hexMode = hex;
    Title = $"Preview — {entryName}";

    if (hex) {
      HexMode.IsChecked = true;
    }

    SizeLabel.Text = FormatSize(data.Length);
    RefreshContent();
  }

  private void RefreshContent() {
    if (_hexMode)
      ContentBox.Text = FormatHex(_data);
    else
      ContentBox.Text = GetEncoding().GetString(_data);
  }

  private void OnModeChanged(object sender, RoutedEventArgs e) {
    if (!IsLoaded) return;
    _hexMode = HexMode.IsChecked == true;
    EncodingBox.IsEnabled = !_hexMode;
    RefreshContent();
  }

  private void OnEncodingChanged(object sender, SelectionChangedEventArgs e) {
    if (!IsLoaded) return;
    if (!_hexMode)
      RefreshContent();
  }

  private Encoding GetEncoding() {
    var selected = (EncodingBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UTF-8";
    return selected switch {
      "ASCII" => Encoding.ASCII,
      "Latin-1" => Encoding.Latin1,
      "UTF-16 LE" => Encoding.Unicode,
      _ => Encoding.UTF8,
    };
  }

  private static string FormatHex(byte[] data) {
    // Limit to 1MB for performance
    var limit = Math.Min(data.Length, 1024 * 1024);
    var sb = new StringBuilder((limit / 16 + 1) * 80);

    for (var offset = 0; offset < limit; offset += 16) {
      sb.Append($"{offset:X8}  ");

      // Hex bytes
      var count = Math.Min(16, limit - offset);
      for (var i = 0; i < 16; i++) {
        if (i < count)
          sb.Append($"{data[offset + i]:X2} ");
        else
          sb.Append("   ");
        if (i == 7) sb.Append(' ');
      }

      sb.Append(' ');

      // ASCII representation
      for (var i = 0; i < count; i++) {
        var b = data[offset + i];
        sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
      }

      sb.AppendLine();
    }

    if (limit < data.Length)
      sb.AppendLine($"\n... truncated at 1 MB ({data.Length:N0} bytes total)");

    return sb.ToString();
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB ({bytes:N0} bytes)",
    _ => $"{bytes / (1024.0 * 1024):F1} MB ({bytes:N0} bytes)",
  };
}
