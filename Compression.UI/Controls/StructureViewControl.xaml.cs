using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compression.Analysis.Structure;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using UserControl = System.Windows.Controls.UserControl;

namespace Compression.UI.Controls;

public partial class StructureViewControl : UserControl {
  private byte[]? _data;
  private bool _suppressTemplateSync;
  private int _structOffset;
  private List<ParsedField>? _lastFields;
  private List<StructureFieldViewModel>? _lastViewModels;
  private int _fieldCounter;

  private static readonly Color[] FieldColors = [
    Color.FromArgb(70, 0x42, 0xA5, 0xF5), // blue
    Color.FromArgb(70, 0x66, 0xBB, 0x6A), // green
    Color.FromArgb(70, 0xFF, 0xA7, 0x26), // orange
    Color.FromArgb(70, 0xAB, 0x47, 0xBC), // purple
    Color.FromArgb(70, 0xEF, 0x53, 0x50), // red
    Color.FromArgb(70, 0x26, 0xC6, 0xDA), // cyan
    Color.FromArgb(70, 0xFF, 0xEE, 0x58), // yellow
    Color.FromArgb(70, 0x8D, 0x6E, 0x63), // brown
    Color.FromArgb(70, 0xEC, 0x40, 0x7A), // pink
    Color.FromArgb(70, 0x78, 0x90, 0x9C), // teal gray
    Color.FromArgb(70, 0x9C, 0xCC, 0x65), // lime
    Color.FromArgb(70, 0x5C, 0x6B, 0xC0), // indigo
  ];

  private static readonly Typeface MonoTypeface = new("Cascadia Mono,Consolas,Courier New");
  private const double HexFontSize = 11;
  private static readonly double PixelsPerDip = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
  private const int ContextRows = 4;

  // ── Type library for accordion ───────────────────────────────────────

  private static readonly (string Category, bool Expanded, (string Name, string Size, string Desc)[] Types)[] TypeLibrary = [
    ("Unsigned Integers", true, [
      ("u8", "1B", "Unsigned 8-bit"),
      ("u16le", "2B", "Unsigned 16-bit LE"),
      ("u16be", "2B", "Unsigned 16-bit BE"),
      ("u32le", "4B", "Unsigned 32-bit LE"),
      ("u32be", "4B", "Unsigned 32-bit BE"),
      ("u64le", "8B", "Unsigned 64-bit LE"),
      ("u64be", "8B", "Unsigned 64-bit BE"),
      ("u96le", "12B", "Unsigned 96-bit LE"),
      ("u96be", "12B", "Unsigned 96-bit BE"),
      ("u128le", "16B", "Unsigned 128-bit LE"),
      ("u128be", "16B", "Unsigned 128-bit BE"),
      ("u256le", "32B", "Unsigned 256-bit LE"),
      ("u256be", "32B", "Unsigned 256-bit BE"),
      ("u512le", "64B", "Unsigned 512-bit LE"),
      ("u512be", "64B", "Unsigned 512-bit BE"),
    ]),
    ("Signed Integers", false, [
      ("i8", "1B", "Signed 8-bit"),
      ("i16le", "2B", "Signed 16-bit LE"),
      ("i16be", "2B", "Signed 16-bit BE"),
      ("i32le", "4B", "Signed 32-bit LE"),
      ("i32be", "4B", "Signed 32-bit BE"),
      ("i64le", "8B", "Signed 64-bit LE"),
      ("i64be", "8B", "Signed 64-bit BE"),
      ("i128le", "16B", "Signed 128-bit LE"),
      ("i128be", "16B", "Signed 128-bit BE"),
    ]),
    ("IEEE Floats", false, [
      ("f16le", "2B", "Half-precision LE (IEEE 754)"),
      ("f16be", "2B", "Half-precision BE (IEEE 754)"),
      ("f32le", "4B", "Single-precision LE"),
      ("f32be", "4B", "Single-precision BE"),
      ("f64le", "8B", "Double-precision LE"),
      ("f64be", "8B", "Double-precision BE"),
    ]),
    ("ML / Tiny Floats", false, [
      ("bf16le", "2B", "BFloat16 LE (brain float, 8-bit exponent)"),
      ("bf16be", "2B", "BFloat16 BE"),
      ("fp8e4m3", "1B", "FP8 E4M3 (4-bit exp, 3-bit mantissa)"),
      ("fp8e5m2", "1B", "FP8 E5M2 (5-bit exp, 2-bit mantissa)"),
    ]),
    ("Fixed-Point", false, [
      ("q8_8le", "2B", "Signed Q8.8 fixed-point LE"),
      ("q16_16le", "4B", "Signed Q16.16 fixed-point LE"),
      ("q32_32le", "8B", "Signed Q32.32 fixed-point LE"),
      ("uq8_8le", "2B", "Unsigned UQ8.8 fixed-point LE"),
      ("uq16_16le", "4B", "Unsigned UQ16.16 fixed-point LE"),
      ("uq32_32le", "8B", "Unsigned UQ32.32 fixed-point LE"),
    ]),
    ("Date & Time", false, [
      ("unixts32le", "4B", "Unix timestamp 32-bit LE (seconds since 1970)"),
      ("unixts32be", "4B", "Unix timestamp 32-bit BE"),
      ("unixts64le", "8B", "Unix timestamp 64-bit LE"),
      ("unixts64be", "8B", "Unix timestamp 64-bit BE"),
      ("dosdate", "2B", "DOS packed date LE (YYYYYYYMMMMDDDDD)"),
      ("dostime", "2B", "DOS packed time LE (HHHHHMMMMMMSSSS0)"),
      ("filetime", "8B", "Windows FILETIME LE (100ns since 1601)"),
      ("oledate", "8B", "OLE Automation date LE (days since 1899-12-30)"),
      ("hfsdate", "4B", "HFS+ / Classic Mac date BE (since 1904)"),
      ("netticks", "8B", ".NET DateTime ticks LE (100ns since 0001)"),
      ("webkittime", "8B", "WebKit timestamp LE (microseconds since 1601)"),
    ]),
    ("BCD / Decimal", false, [
      ("bcd8", "1B", "Packed BCD 2-digit"),
      ("bcd16le", "2B", "Packed BCD 4-digit LE"),
      ("bcd32le", "4B", "Packed BCD 8-digit LE"),
    ]),
    ("Color", false, [
      ("rgb24", "3B", "RGB 24-bit (R,G,B)"),
      ("rgba32", "4B", "RGBA 32-bit (R,G,B,A)"),
      ("bgr24", "3B", "BGR 24-bit (B,G,R)"),
      ("bgra32", "4B", "BGRA 32-bit (B,G,R,A)"),
      ("rgb565le", "2B", "RGB 5-6-5 packed LE"),
      ("rgb555le", "2B", "RGB 5-5-5 packed LE"),
      ("rgba5551le", "2B", "RGBA 5-5-5-1 packed LE"),
    ]),
    ("Network & ID", false, [
      ("ipv4", "4B", "IPv4 address (dotted decimal)"),
      ("ipv6", "16B", "IPv6 address (colon-hex)"),
      ("mac48", "6B", "MAC-48 / EUI-48 address"),
      ("fourcc", "4B", "FourCC code (4 ASCII chars)"),
      ("guid", "16B", "GUID / UUID (mixed-endian)"),
    ]),
    ("Special", false, [
      ("bool8", "1B", "Boolean byte (0=false)"),
    ]),
    ("Arrays & Bitfields", false, [
      ("bits[1]", "var", "N-bit bitfield (change N)"),
      ("char[1]", "var", "ASCII string (change N)"),
      ("u8[1]", "var", "Byte array (change N)"),
    ]),
  ];

  public StructureViewControl() {
    InitializeComponent();
    foreach (var name in BuiltInTemplates.All.Keys)
      TemplateBox.Items.Add(name);
    TemplateBox.Items.Add("Custom");
    if (TemplateBox.Items.Count > 1)
      TemplateBox.SelectedIndex = 0;
    TemplateEditor.TextChanged += OnTemplateEditorTextChanged;
    BuildTypeAccordion();
  }

  /// <summary>Sets the binary data to interpret.</summary>
  public byte[]? Data {
    get => _data;
    set {
      _data = value;
      AutoParse();
    }
  }

  // ── Type accordion ───────────────────────────────────────────────────

  private void BuildTypeAccordion() {
    var monoFont = new FontFamily("Cascadia Mono,Consolas,Courier New");
    foreach (var (category, expanded, types) in TypeLibrary) {
      var wrap = new WrapPanel();
      foreach (var (name, size, desc) in types) {
        var btn = new Button {
          Content = name,
          MinWidth = 58,
          Margin = new Thickness(1),
          Padding = new Thickness(3, 1, 3, 1),
          FontFamily = monoFont,
          FontSize = 10,
          ToolTip = $"{desc} ({size})",
          Tag = name,
        };
        btn.Click += OnTypeButtonClick;
        wrap.Children.Add(btn);
      }
      var expander = new Expander {
        Header = category,
        Content = wrap,
        IsExpanded = expanded,
        FontSize = 11,
        Padding = new Thickness(0, 0, 0, 2),
      };
      TypeAccordion.Children.Add(expander);
    }
  }

  private void OnTypeButtonClick(object sender, RoutedEventArgs e) {
    if (sender is not Button btn || btn.Tag is not string typeName) return;
    var fieldName = $"field{_fieldCounter++}";
    var text = $"  {typeName} {fieldName};\n";
    var pos = TemplateEditor.CaretIndex;
    TemplateEditor.Text = TemplateEditor.Text.Insert(pos, text);
    TemplateEditor.CaretIndex = pos + text.Length;
    TemplateEditor.Focus();
  }

  // ── Template selection / editing ─────────────────────────────────────

  private void OnTemplateSelected(object sender, SelectionChangedEventArgs e) {
    if (_suppressTemplateSync) return;
    if (TemplateBox.SelectedItem is string name && BuiltInTemplates.All.TryGetValue(name, out var source)) {
      _suppressTemplateSync = true;
      TemplateEditor.Text = source;
      _suppressTemplateSync = false;
      AutoParse();
    }
  }

  private void OnTemplateEditorTextChanged(object sender, TextChangedEventArgs e) {
    if (_suppressTemplateSync) return;
    var selectedName = TemplateBox.SelectedItem as string;
    if (selectedName != "Custom") {
      if (selectedName == null || !BuiltInTemplates.All.TryGetValue(selectedName, out var builtIn) || TemplateEditor.Text != builtIn) {
        _suppressTemplateSync = true;
        TemplateBox.SelectedItem = "Custom";
        _suppressTemplateSync = false;
      }
    }
    AutoParse();
  }

  // ── Offset spinner ───────────────────────────────────────────────────

  private void OnOffsetPreviewTextInput(object sender, TextCompositionEventArgs e) {
    e.Handled = !int.TryParse(e.Text, out _) && e.Text != "-";
  }

  private void OnOffsetChanged(object sender, TextChangedEventArgs e) {
    if (!int.TryParse(OffsetBox.Text, out var val)) return;
    val = Math.Max(0, val);
    if (val == _structOffset) return;
    _structOffset = val;
    AutoParse();
  }

  private void OnOffsetUp(object sender, RoutedEventArgs e) {
    // Don't set _structOffset directly — let OnOffsetChanged handle it
    OffsetBox.Text = Math.Max(0, _structOffset + 1).ToString();
  }

  private void OnOffsetDown(object sender, RoutedEventArgs e) {
    OffsetBox.Text = Math.Max(0, _structOffset - 1).ToString();
  }

  // ── Parse ────────────────────────────────────────────────────────────

  private void AutoParse() {
    if (_data == null || _data.Length == 0) return;
    var source = TemplateEditor.Text;
    if (string.IsNullOrWhiteSpace(source)) return;
    try {
      var template = TemplateParser.Parse(source, TemplateBox.SelectedItem?.ToString() ?? "Custom");
      var offset = Math.Min(_structOffset, _data.Length - 1);
      if (offset < 0) offset = 0;
      var fields = StructureInterpreter.Interpret(template, _data, offset);
      _lastFields = fields;
      _lastViewModels = ToViewModels(fields, 0);
      ResultTree.ItemsSource = _lastViewModels;
      DrawStructHex();
    }
    catch {
      // Silently ignore parse errors during live editing
    }
  }

  private void OnParse(object sender, RoutedEventArgs e) {
    if (_data == null || _data.Length == 0) return;
    var source = TemplateEditor.Text;
    if (string.IsNullOrWhiteSpace(source)) return;
    try {
      var template = TemplateParser.Parse(source, TemplateBox.SelectedItem?.ToString() ?? "Custom");
      var offset = Math.Min(_structOffset, _data.Length - 1);
      if (offset < 0) offset = 0;
      var fields = StructureInterpreter.Interpret(template, _data, offset);
      _lastFields = fields;
      _lastViewModels = ToViewModels(fields, 0);
      ResultTree.ItemsSource = _lastViewModels;
      DrawStructHex();
    }
    catch (Exception ex) {
      MessageBox.Show($"Parse error: {ex.Message}", "Template Error",
        MessageBoxButton.OK, MessageBoxImage.Warning);
    }
  }

  // ── Save / Load ──────────────────────────────────────────────────────

  private void OnSaveTemplate(object sender, RoutedEventArgs e) {
    var dlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Save Template",
      Filter = "CWB Template|*.cwbt|All Files|*.*",
      DefaultExt = ".cwbt",
      FileName = (TemplateBox.SelectedItem as string ?? "custom") + ".cwbt",
    };
    if (dlg.ShowDialog() == true)
      File.WriteAllText(dlg.FileName, TemplateEditor.Text);
  }

  private void OnLoadTemplate(object sender, RoutedEventArgs e) {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Load Template",
      Filter = "CWB Template|*.cwbt|All Files|*.*",
    };
    if (dlg.ShowDialog() == true) {
      _suppressTemplateSync = true;
      TemplateEditor.Text = File.ReadAllText(dlg.FileName);
      TemplateBox.SelectedItem = "Custom";
      _suppressTemplateSync = false;
      AutoParse();
    }
  }

  // ── Hex viewer with field coloring + context rows ────────────────────

  private const int BytesPerRow = 16;
  private int[]? _byteFieldMap;
  private string[]? _fieldNames;
  private int _hexLineCount;

  private void DrawStructHex() {
    StructHexCanvas.Children.Clear();
    if (_data == null || _lastFields == null) return;

    var fieldMap = new int[_data.Length];
    Array.Fill(fieldMap, -1);
    var fieldNameList = new List<string>();
    BuildFieldMap(_lastFields, fieldMap, fieldNameList, 0);
    _byteFieldMap = fieldMap;
    _fieldNames = fieldNameList.ToArray();

    // Compute struct data range
    var structStart = _structOffset;
    var structEnd = structStart;
    if (_lastFields.Count > 0) {
      foreach (var f in FlattenFields(_lastFields))
        structEnd = Math.Max(structEnd, f.Offset + f.Size);
    }

    // Add context rows above and below
    var structRowStart = (structStart / BytesPerRow) * BytesPerRow;
    var structRowEnd = ((structEnd + BytesPerRow - 1) / BytesPerRow) * BytesPerRow;
    if (structRowEnd <= structRowStart) structRowEnd = structRowStart + BytesPerRow * 2;

    var displayStart = Math.Max(0, structRowStart - ContextRows * BytesPerRow);
    var displayEnd = Math.Min(_data.Length, structRowEnd + ContextRows * BytesPerRow);

    var charWidth = MeasureCharWidth();
    var lineHeight = HexFontSize + 4;
    var offsetWidth = _data.Length > 0xFFFF ? 8 : 4;
    var hexStartX = (offsetWidth + 2) * charWidth;
    var asciiStartX = hexStartX + BytesPerRow * 3 * charWidth + charWidth * 2;
    var totalWidth = asciiStartX + BytesPerRow * charWidth + charWidth;
    _hexLineCount = (displayEnd - displayStart + BytesPerRow - 1) / BytesPerRow;

    StructHexCanvas.Width = totalWidth;
    StructHexCanvas.Height = _hexLineCount * lineHeight + 4;

    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen()) {
      var row = 0;
      for (var lineOffset = displayStart; lineOffset < displayEnd; lineOffset += BytesPerRow) {
        var y = row * lineHeight + 2;
        var isContext = lineOffset < structRowStart || lineOffset >= structRowEnd;

        var offsetText = CreateText(lineOffset.ToString($"X{offsetWidth}"),
          isContext ? Brushes.LightGray : Brushes.Gray);
        dc.DrawText(offsetText, new Point(2, y));

        var count = Math.Min(BytesPerRow, _data.Length - lineOffset);
        for (var i = 0; i < count; i++) {
          var byteIdx = lineOffset + i;
          var b = _data[byteIdx];

          var hexX = hexStartX + i * 3 * charWidth;
          if (!isContext && fieldMap[byteIdx] >= 0) {
            var colorIdx = fieldMap[byteIdx] % FieldColors.Length;
            dc.DrawRectangle(new SolidColorBrush(FieldColors[colorIdx]), null,
              new Rect(hexX - 1, y, charWidth * 2.5, lineHeight));
          }
          var hexFg = isContext ? Brushes.Silver : Brushes.Black;
          var hexText = CreateText(b.ToString("X2"), hexFg);
          dc.DrawText(hexText, new Point(hexX, y));

          var asciiX = asciiStartX + i * charWidth;
          if (!isContext && fieldMap[byteIdx] >= 0) {
            var colorIdx = fieldMap[byteIdx] % FieldColors.Length;
            var asciiColor = FieldColors[colorIdx];
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, asciiColor.R, asciiColor.G, asciiColor.B)),
              null, new Rect(asciiX - 0.5, y, charWidth, lineHeight));
          }
          var ch = b is >= 0x20 and < 0x7F ? (char)b : '.';
          var asciiFg = isContext ? Brushes.Silver : Brushes.DarkGray;
          var asciiText = CreateText(ch.ToString(), asciiFg);
          dc.DrawText(asciiText, new Point(asciiX, y));
        }

        row++;
      }
    }

    var rtb = new RenderTargetBitmap(
      (int)Math.Ceiling(totalWidth * PixelsPerDip),
      (int)Math.Ceiling(StructHexCanvas.Height * PixelsPerDip),
      96 * PixelsPerDip, 96 * PixelsPerDip, PixelFormats.Pbgra32);
    rtb.Render(dv);

    var img = new System.Windows.Controls.Image {
      Source = rtb,
      Width = totalWidth,
      Height = StructHexCanvas.Height,
    };
    Canvas.SetLeft(img, 0);
    Canvas.SetTop(img, 0);
    StructHexCanvas.Children.Add(img);

    _hexDisplayStart = displayStart;
    _hexDisplayEnd = displayEnd;
    _hexCharWidth = charWidth;
    _hexLineHeight = lineHeight;
    _hexOffsetWidth = offsetWidth;
  }

  private int _hexDisplayStart, _hexDisplayEnd;
  private double _hexCharWidth, _hexLineHeight;
  private int _hexOffsetWidth;

  private void OnHexCanvasMouseMove(object sender, MouseEventArgs e) {
    if (_data == null || _byteFieldMap == null) return;
    var pos = e.GetPosition(StructHexCanvas);
    var row = (int)((pos.Y - 2) / _hexLineHeight);
    var hexStartX = (_hexOffsetWidth + 2) * _hexCharWidth;

    var col = (int)((pos.X - hexStartX) / (3 * _hexCharWidth));
    if (col < 0 || col >= BytesPerRow || row < 0) {
      HexTooltip.IsOpen = false;
      return;
    }

    var byteIdx = _hexDisplayStart + row * BytesPerRow + col;
    if (byteIdx < 0 || byteIdx >= _data.Length) {
      HexTooltip.IsOpen = false;
      return;
    }

    var fieldIdx = _byteFieldMap[byteIdx];
    var fieldName = fieldIdx >= 0 && fieldIdx < _fieldNames!.Length ? _fieldNames[fieldIdx] : "(no field)";
    HexTooltip.Content = $"Offset: 0x{byteIdx:X} ({byteIdx})\nByte: 0x{_data[byteIdx]:X2} ({_data[byteIdx]})\nField: {fieldName}";
    HexTooltip.IsOpen = true;
  }

  private void OnHexCanvasMouseLeave(object sender, MouseEventArgs e) {
    HexTooltip.IsOpen = false;
  }

  private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) {
    if (e.NewValue is StructureFieldViewModel vm && _data != null) {
      if (int.TryParse(vm.OffsetDisplay.Replace("0x", ""), NumberStyles.HexNumber, null, out var fieldOff)) {
        var lineHeight = _hexLineHeight > 0 ? _hexLineHeight : HexFontSize + 4;
        var row = (fieldOff - _hexDisplayStart) / BytesPerRow;
        HexScroll.ScrollToVerticalOffset(row * lineHeight);
      }
    }
  }

  // ── Field map builder ────────────────────────────────────────────────

  private static void BuildFieldMap(List<ParsedField> fields, int[] map, List<string> names, int depth) {
    foreach (var f in fields) {
      if (f.Children != null && f.Children.Count > 0 && f.Size == 0) {
        BuildFieldMap(f.Children, map, names, depth);
      }
      else {
        var idx = names.Count;
        names.Add(f.Name);
        var end = Math.Min(f.Offset + f.Size, map.Length);
        for (var i = f.Offset; i < end; i++)
          map[i] = idx;
        if (f.Children != null) {
          foreach (var child in f.Children) {
            var childEnd = Math.Min(child.Offset + child.Size, map.Length);
            for (var i = child.Offset; i < childEnd; i++)
              map[i] = idx;
          }
        }
      }
    }
  }

  private static IEnumerable<ParsedField> FlattenFields(List<ParsedField> fields) {
    foreach (var f in fields) {
      yield return f;
      if (f.Children != null)
        foreach (var c in FlattenFields(f.Children))
          yield return c;
    }
  }

  // ── View model builder ───────────────────────────────────────────────

  private static List<StructureFieldViewModel> ToViewModels(List<ParsedField> fields, int colorStart) {
    var result = new List<StructureFieldViewModel>();
    var idx = colorStart;
    foreach (var f in fields) {
      var hasOwnBytes = f.Size > 0 || (f.Children == null || f.Children.Count == 0);
      var colorIdx = hasOwnBytes ? idx++ : -1;
      var color = colorIdx >= 0
        ? FieldColors[colorIdx % FieldColors.Length]
        : Color.FromArgb(0, 0, 0, 0);
      result.Add(new StructureFieldViewModel {
        Name = f.Name,
        TypeName = f.TypeName,
        OffsetDisplay = $"0x{f.Offset:X4}",
        DisplayValue = f.DisplayValue ?? "",
        ColorBrush = new SolidColorBrush(color),
        Children = f.Children != null ? ToViewModels(f.Children, idx) : null,
      });
      if (f.Children != null && !hasOwnBytes) {
        idx += CountLeafFields(f.Children);
      }
    }
    return result;
  }

  private static int CountLeafFields(List<ParsedField> fields) {
    var count = 0;
    foreach (var f in fields) {
      if (f.Size > 0 || f.Children == null || f.Children.Count == 0) count++;
      else if (f.Children != null) count += CountLeafFields(f.Children);
    }
    return count;
  }

  // ── Text rendering helpers ───────────────────────────────────────────

  private static FormattedText CreateText(string text, Brush foreground) =>
    new(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
      MonoTypeface, HexFontSize, foreground, PixelsPerDip);

  private static double MeasureCharWidth() => CreateText("0", Brushes.Black).Width;
}

internal sealed class StructureFieldViewModel {
  public required string Name { get; init; }
  public required string TypeName { get; init; }
  public required string OffsetDisplay { get; init; }
  public required string DisplayValue { get; init; }
  public required Brush ColorBrush { get; init; }
  public List<StructureFieldViewModel>? Children { get; init; }
}
