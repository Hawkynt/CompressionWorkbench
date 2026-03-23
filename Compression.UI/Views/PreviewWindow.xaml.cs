using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Compression.Analysis.Statistics;
using Compression.UI.Controls;

namespace Compression.UI.Views;

public partial class PreviewWindow : Window {
  private byte[] _data = [];
  private bool _hexMode;
  private List<HexLineViewModel>? _hexLines;
  private int _bytesPerRow = 16;
  private bool _autoWidth = true;

  public PreviewWindow() {
    InitializeComponent();
  }

  public void ShowData(string entryName, byte[] data, bool hex = false) {
    _data = data;
    _hexMode = hex;
    _hexLines = null;
    Title = $"Preview \u2014 {entryName}";

    if (hex)
      HexMode.IsChecked = true;

    SizeLabel.Text = FormatSize(data.Length);
    SizeInfo.Text = FormatSize(data.Length);
    RefreshContent();
  }

  private void RefreshContent() {
    if (_hexMode) {
      ContentBox.Visibility = Visibility.Collapsed;
      HexList.Visibility = Visibility.Visible;
      EncodingBox.IsEnabled = false;

      UpdateFrequencyPercentiles();
      _hexLines = BuildHexLines(_data, _bytesPerRow);
      HexList.DataContext = _hexLines;
    }
    else {
      HexList.Visibility = Visibility.Collapsed;
      ContentBox.Visibility = Visibility.Visible;
      EncodingBox.IsEnabled = true;

      var maxTextBytes = Math.Min(_data.Length, 10 * 1024 * 1024);
      var text = GetEncoding().GetString(_data, 0, maxTextBytes);
      if (maxTextBytes < _data.Length)
        text += $"\n\n... truncated at 10 MB ({_data.Length:N0} bytes total)";
      ContentBox.Text = text;
    }
  }

  private void OnModeChanged(object sender, RoutedEventArgs e) {
    if (!IsLoaded) return;
    _hexMode = HexMode.IsChecked == true;
    _hexLines = null;
    if (_hexMode && _autoWidth) RecalcAutoWidth();
    RefreshContent();
  }

  private void OnEncodingChanged(object sender, SelectionChangedEventArgs e) {
    if (!IsLoaded) return;
    if (!_hexMode)
      RefreshContent();
  }

  private void OnWrapChanged(object sender, RoutedEventArgs e) {
    if (!IsLoaded) return;
    ContentBox.TextWrapping = WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
  }

  private void OnBytesPerRowChanged(object sender, SelectionChangedEventArgs e) {
    if (!IsLoaded) return;
    var selected = (BytesPerRowBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto";
    if (selected == "Auto") {
      _autoWidth = true;
      RecalcAutoWidth();
    }
    else if (int.TryParse(selected, out var bpr) && bpr > 0) {
      _autoWidth = false;
      _bytesPerRow = bpr;
    }
    _hexLines = null;
    if (_hexMode) RefreshContent();
  }

  private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) {
    if (!IsLoaded || !_hexMode || !_autoWidth) return;
    var oldBpr = _bytesPerRow;
    RecalcAutoWidth();
    if (_bytesPerRow != oldBpr) {
      _hexLines = null;
      RefreshContent();
    }
  }

  private void RecalcAutoWidth() {
    // Estimate: each byte takes ~3 chars in hex + 1 char in ASCII + offset (10) + separators
    // Monospace char width ~ 7.2px at font size 12
    var availableWidth = HexList.ActualWidth > 50 ? HexList.ActualWidth - 40 : ActualWidth - 60;
    if (availableWidth <= 0) availableWidth = 800;
    const double charWidth = 7.2;
    // Per byte: "XX " (3 chars hex) + 1 char ascii = 4 chars. Plus offset (10 chars) + gaps
    var charsPerByte = 4.0;
    var fixedChars = 13.0; // offset + separators
    var maxBytes = (int)((availableWidth / charWidth - fixedChars) / charsPerByte);
    _bytesPerRow = Math.Max(8, maxBytes);
  }

  private bool _analyzeMode;

  public void ShowData(string entryName, byte[] data, bool hex, bool analyzeMode) {
    _analyzeMode = analyzeMode;
    ShowData(entryName, data, hex);
    if (analyzeMode) {
      StatsToggle.IsChecked = true;
      // Force show stats immediately even if window isn't fully loaded yet
      ApplyStatsVisibility(true);
    }
  }

  private void OnStatsToggled(object sender, RoutedEventArgs e) {
    ApplyStatsVisibility(StatsToggle.IsChecked == true);
  }

  private void ApplyStatsVisibility(bool show) {
    if (show) {
      SplitterColumn.Width = new GridLength(4);
      StatsSplitter.Visibility = Visibility.Visible;
      StatsColumn.Width = new GridLength(280);
      StatsPanel.Visibility = Visibility.Visible;
      if (_data.Length > 0)
        StatsControl.Data = _data;
    }
    else {
      SplitterColumn.Width = new GridLength(0);
      StatsSplitter.Visibility = Visibility.Collapsed;
      StatsColumn.Width = new GridLength(0);
      StatsPanel.Visibility = Visibility.Collapsed;
    }
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

  private void UpdateFrequencyPercentiles() {
    if (_data.Length == 0) return;
    var freq = BinaryStatistics.ComputeByteFrequency(_data);

    // Build sorted frequency list for percentile mapping
    var sorted = new long[256];
    Array.Copy(freq, sorted, 256);
    Array.Sort(sorted);

    var percentiles = new byte[256];
    for (var i = 0; i < 256; i++) {
      var rank = Array.BinarySearch(sorted, freq[i]);
      // Handle duplicates: find first occurrence
      while (rank > 0 && sorted[rank - 1] == freq[i]) rank--;
      percentiles[i] = (byte)(rank * 255 / 255);
    }

    HexLineControl.FrequencyPercentiles = percentiles;
    HexLineControl.ColorizeHex = _analyzeMode;
  }

  private static List<HexLineViewModel> BuildHexLines(byte[] data, int bytesPerRow) {
    var lineCount = (data.Length + bytesPerRow - 1) / bytesPerRow;
    var lines = new List<HexLineViewModel>(lineCount);
    var offsetWidth = data.Length > 0xFFFFFF ? 8 : (data.Length > 0xFFFF ? 6 : 4);

    for (var offset = 0; offset < data.Length; offset += bytesPerRow) {
      var count = Math.Min(bytesPerRow, data.Length - offset);
      var bytes = new HexByte[count];

      for (var i = 0; i < count; i++) {
        var b = data[offset + i];
        bytes[i] = new HexByte(b, b.ToString("X2"), b is >= 0x20 and < 0x7F ? (char)b : '.');
      }

      lines.Add(new HexLineViewModel {
        OffsetText = offset.ToString($"X{offsetWidth}"),
        Bytes = bytes,
        ByteCount = count,
        BytesPerRow = bytesPerRow,
      });
    }

    return lines;
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB ({bytes:N0} bytes)",
    _ => $"{bytes / (1024.0 * 1024):F1} MB ({bytes:N0} bytes)",
  };
}
