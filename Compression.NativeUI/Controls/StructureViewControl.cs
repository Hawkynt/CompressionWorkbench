using System.Drawing;
using System.Globalization;
using Compression.Analysis.Structure;
using Compression.NativeUI.Theming;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// Interprets the buffer through a struct template: edit the template on the left, pick field types
/// from the accordion on the right, and see the parsed fields both as a colour-washed hex dump and
/// as a value tree.
/// </summary>
internal sealed class StructureViewControl : Panel {
  private const int EditorRowHeight = 180;
  private static readonly Font MonoFont = new("Cascadia Mono", 8.5f, FontStyle.Regular);

  /// <summary>The field types offered by the accordion, grouped as the WPF control grouped them.</summary>
  private static readonly (string Category, bool Expanded, (string Name, string Size, string Desc)[] Types)[] TypeLibrary = [
    ("Unsigned Integers", true, [
      ("u8", "1B", "Unsigned 8-bit"),
      ("u16le", "2B", "Unsigned 16-bit LE"), ("u16be", "2B", "Unsigned 16-bit BE"),
      ("u32le", "4B", "Unsigned 32-bit LE"), ("u32be", "4B", "Unsigned 32-bit BE"),
      ("u64le", "8B", "Unsigned 64-bit LE"), ("u64be", "8B", "Unsigned 64-bit BE"),
      ("u96le", "12B", "Unsigned 96-bit LE"), ("u96be", "12B", "Unsigned 96-bit BE"),
      ("u128le", "16B", "Unsigned 128-bit LE"), ("u128be", "16B", "Unsigned 128-bit BE"),
      ("u256le", "32B", "Unsigned 256-bit LE"), ("u256be", "32B", "Unsigned 256-bit BE"),
      ("u512le", "64B", "Unsigned 512-bit LE"), ("u512be", "64B", "Unsigned 512-bit BE"),
    ]),
    ("Signed Integers", false, [
      ("i8", "1B", "Signed 8-bit"),
      ("i16le", "2B", "Signed 16-bit LE"), ("i16be", "2B", "Signed 16-bit BE"),
      ("i32le", "4B", "Signed 32-bit LE"), ("i32be", "4B", "Signed 32-bit BE"),
      ("i64le", "8B", "Signed 64-bit LE"), ("i64be", "8B", "Signed 64-bit BE"),
      ("i128le", "16B", "Signed 128-bit LE"), ("i128be", "16B", "Signed 128-bit BE"),
    ]),
    ("IEEE Floats", false, [
      ("f16le", "2B", "Half-precision LE (IEEE 754)"), ("f16be", "2B", "Half-precision BE (IEEE 754)"),
      ("f32le", "4B", "Single-precision LE"), ("f32be", "4B", "Single-precision BE"),
      ("f64le", "8B", "Double-precision LE"), ("f64be", "8B", "Double-precision BE"),
    ]),
    ("ML / Tiny Floats", false, [
      ("bf16le", "2B", "BFloat16 LE (brain float, 8-bit exponent)"), ("bf16be", "2B", "BFloat16 BE"),
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
      ("rgb24", "3B", "RGB 24-bit (R,G,B)"), ("rgba32", "4B", "RGBA 32-bit (R,G,B,A)"),
      ("bgr24", "3B", "BGR 24-bit (B,G,R)"), ("bgra32", "4B", "BGRA 32-bit (B,G,R,A)"),
      ("rgb565le", "2B", "RGB 5-6-5 packed LE"), ("rgb555le", "2B", "RGB 5-5-5 packed LE"),
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

  private readonly ComboBox _templateBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly TextBox _offsetBox = new() { Text = "0" };
  private readonly Button _offsetUp = new() { Text = "+" };
  private readonly Button _offsetDown = new() { Text = "-" };
  private readonly Button _parse = new() { Text = "Parse" };
  private readonly Button _save = new() { Text = "Save..." };
  private readonly Button _load = new() { Text = "Load..." };

  private readonly TextBox _editor = new() {
    Multiline = true,
    AcceptsReturn = true,
    AcceptsTab = true,
    Font = MonoFont,
  };

  private readonly Accordion _typeAccordion = new();
  private readonly StructureHexView _hexView = new();
  private readonly TreeListView _resultTree = new() { ShowColumnHeaders = true, ItemHeight = 18 };
  private readonly ToolTip _toolTips = new();

  private byte[]? _data;
  private bool _suppressTemplateSync;
  private int _structOffset;
  private List<ParsedField>? _lastFields;
  private int _fieldCounter;

  public StructureViewControl() {
    foreach (var name in BuiltInTemplates.All.Keys) this._templateBox.Items.Add(name);
    this._templateBox.Items.Add("Custom");
    if (this._templateBox.Items.Count > 1) this._templateBox.SelectedIndex = 0;
    this._templateBox.SelectedIndexChanged += (_, _) => this.OnTemplateSelected();

    this._editor.TextChanged += (_, _) => this.OnTemplateEditorTextChanged();
    this._offsetBox.TextChanged += (_, _) => this.OnOffsetChanged();
    this._offsetUp.Click += (_, _) => this._offsetBox.Text = Math.Max(0, this._structOffset + 1).ToString();
    this._offsetDown.Click += (_, _) => this._offsetBox.Text = Math.Max(0, this._structOffset - 1).ToString();
    this._parse.Click += (_, _) => this.Parse(reportErrors: true);
    this._save.Click += (_, _) => this.SaveTemplate();
    this._load.Click += (_, _) => this.LoadTemplate();

    this.BuildTypeAccordion();
    this.BuildResultTree();

    this.Controls.AddRange(
      new Label { Text = "Template:" }, this._templateBox,
      new Label { Text = "Offset:" }, this._offsetBox, this._offsetUp, this._offsetDown,
      this._parse, this._save, this._load,
      this._editor, this._typeAccordion, this._hexView, this._resultTree);

    // Control has no resize event, so everything below the toolbar follows the panel by anchoring.
    this._editor.Anchor = AnchorStyles.Top | AnchorStyles.Left;
    this._typeAccordion.Anchor = AnchorStyles.Top | AnchorStyles.Right;
    this._hexView.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
    this._resultTree.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;

    this.LayoutChildren();
  }

  /// <summary>The buffer to interpret.</summary>
  public byte[]? Data {
    get => this._data;
    set {
      this._data = value;
      this.Parse(reportErrors: false);
    }
  }

  private void LayoutChildren() {
    var width = Math.Max(400, this.Width);
    var controls = this.Controls;

    controls[0].Bounds = new(0, 6, 60, 22);       // "Template:"
    this._templateBox.Bounds = new(62, 4, 180, 24);
    controls[2].Bounds = new(250, 6, 44, 22);     // "Offset:"
    this._offsetBox.Bounds = new(296, 4, 70, 24);
    this._offsetUp.Bounds = new(368, 4, 18, 12);
    this._offsetDown.Bounds = new(368, 16, 18, 12);
    this._parse.Bounds = new(392, 4, 60, 24);
    this._save.Bounds = new(456, 4, 64, 24);
    this._load.Bounds = new(524, 4, 64, 24);

    // Editor and type accordion split the upper band two-to-one.
    var editorWidth = width * 2 / 3;
    this._editor.Bounds = new(0, 34, editorWidth - 4, EditorRowHeight);
    this._typeAccordion.Bounds = new(editorWidth, 34, width - editorWidth, EditorRowHeight);

    // Hex dump and value tree split the lower band evenly.
    var lowerY = 34 + EditorRowHeight + 6;
    var lowerHeight = Math.Max(120, this.Height - lowerY);
    var half = width / 2;
    this._hexView.Bounds = new(0, lowerY, half - 4, lowerHeight);
    this._resultTree.Bounds = new(half, lowerY, width - half, lowerHeight);
  }

  private void BuildTypeAccordion() {
    foreach (var (category, expanded, types) in TypeLibrary) {
      var pane = new AccordionPane(category) { Expanded = expanded };
      var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };

      foreach (var (name, size, desc) in types) {
        var button = new Button { Text = name, Font = MonoFont, Size = new(58, 20), Tag = name };
        this._toolTips.SetToolTip(button, $"{desc} ({size})");
        button.Click += (_, _) => this.InsertType(name);
        flow.Controls.Add(button);
      }

      pane.Controls.Add(flow);
      this._typeAccordion.Panes.Add(pane);
    }
  }

  /// <summary>Appends a field of the clicked type to the template being edited.</summary>
  private void InsertType(string typeName) {
    var text = $"  {typeName} field{this._fieldCounter++};{Environment.NewLine}";
    var caret = this._editor.SelectionStart;
    this._editor.Text = this._editor.Text.Insert(Math.Clamp(caret, 0, this._editor.Text.Length), text);
    this._editor.SelectionStart = caret + text.Length;
    this._editor.Focus();
  }

  private void BuildResultTree() {
    this._resultTree.Columns.Add(new TreeListViewColumn("Offset", 70, static n => Field(n)?.OffsetDisplay ?? ""));
    this._resultTree.Columns.Add(new TreeListViewColumn("Type", 80, static n => Field(n)?.TypeName ?? ""));
    this._resultTree.Columns.Add(new TreeListViewColumn("Name", 120, static n => Field(n)?.Name ?? n.Text));
    this._resultTree.Columns.Add(new TreeListViewColumn("Value", 200, static n => Field(n)?.DisplayValue ?? ""));

    this._resultTree.AfterSelect += (_, e) => {
      if (Field(e.Node) is not { } field) return;
      if (int.TryParse(field.OffsetDisplay.Replace("0x", ""), NumberStyles.HexNumber, null, out var offset))
        this._hexView.ScrollToOffset(offset);
    };
  }

  private static StructureFieldViewModel? Field(TreeNode? node) => node?.Tag as StructureFieldViewModel;

  private void OnTemplateSelected() {
    if (this._suppressTemplateSync) return;
    if (this._templateBox.SelectedItem is not string name) return;
    if (!BuiltInTemplates.All.TryGetValue(name, out var source)) return;

    this._suppressTemplateSync = true;
    this._editor.Text = source;
    this._suppressTemplateSync = false;
    this.Parse(reportErrors: false);
  }

  /// <summary>Editing a built-in template silently switches the selection to Custom.</summary>
  private void OnTemplateEditorTextChanged() {
    if (this._suppressTemplateSync) return;

    var selected = this._templateBox.SelectedItem as string;
    if (selected != "Custom"
        && (selected is null || !BuiltInTemplates.All.TryGetValue(selected, out var builtIn) || this._editor.Text != builtIn)) {
      this._suppressTemplateSync = true;
      this._templateBox.SelectedItem = "Custom";
      this._suppressTemplateSync = false;
    }

    this.Parse(reportErrors: false);
  }

  private void OnOffsetChanged() {
    if (!int.TryParse(this._offsetBox.Text, out var value)) return;
    value = Math.Max(0, value);
    if (value == this._structOffset) return;

    this._structOffset = value;
    this.Parse(reportErrors: false);
  }

  /// <summary>
  /// Re-parses and redraws. Live edits swallow parse errors — half-typed templates are expected —
  /// while the explicit Parse button reports them.
  /// </summary>
  private void Parse(bool reportErrors) {
    if (this._data is not { Length: > 0 } data) return;

    var source = this._editor.Text;
    if (string.IsNullOrWhiteSpace(source)) return;

    try {
      var template = TemplateParser.Parse(source, this._templateBox.SelectedItem?.ToString() ?? "Custom");
      var offset = Math.Clamp(this._structOffset, 0, data.Length - 1);
      var fields = StructureInterpreter.Interpret(template, data, offset);

      this._lastFields = fields;
      this.PopulateTree(ToViewModels(fields, 0));
      this._hexView.SetStructure(data, fields, offset);
    } catch (Exception ex) when (!reportErrors) {
      _ = ex;
    } catch (Exception ex) {
      MessageBox.Show($"Parse error: {ex.Message}", "Template Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
  }

  private void PopulateTree(List<StructureFieldViewModel> models) {
    this._resultTree.Nodes.Clear();
    foreach (var node in BuildNodes(models)) this._resultTree.Nodes.Add(node);
    this._resultTree.ExpandAll();
  }

  private static IEnumerable<TreeNode> BuildNodes(List<StructureFieldViewModel> models) {
    foreach (var model in models) {
      var node = new TreeNode(model.Name) { Tag = model };
      if (model.Children is { } children)
        foreach (var child in BuildNodes(children))
          node.Nodes.Add(child);
      yield return node;
    }
  }

  private void SaveTemplate() {
    var dialog = new SaveFileDialog {
      Title = "Save Template",
      Filter = "CWB Template|*.cwbt|All Files|*.*",
      FileName = (this._templateBox.SelectedItem as string ?? "custom") + ".cwbt",
    };
    if (dialog.ShowDialog() == DialogResult.OK)
      File.WriteAllText(dialog.FileName, this._editor.Text);
  }

  private void LoadTemplate() {
    var dialog = new OpenFileDialog { Title = "Load Template", Filter = "CWB Template|*.cwbt|All Files|*.*" };
    if (dialog.ShowDialog() != DialogResult.OK) return;

    this._suppressTemplateSync = true;
    this._editor.Text = File.ReadAllText(dialog.FileName);
    this._templateBox.SelectedItem = "Custom";
    this._suppressTemplateSync = false;
    this.Parse(reportErrors: false);
  }

  /// <summary>
  /// Assigns each leaf field the next colour in the cycle, so the tree swatches line up with the
  /// washes in the hex dump. A group that owns no bytes of its own takes no colour.
  /// </summary>
  private static List<StructureFieldViewModel> ToViewModels(List<ParsedField> fields, int colorStart) {
    var result = new List<StructureFieldViewModel>();
    var index = colorStart;

    foreach (var f in fields) {
      var hasOwnBytes = f.Size > 0 || f.Children is not { Count: > 0 };
      var colorIndex = hasOwnBytes ? index++ : -1;

      result.Add(new() {
        Name = f.Name,
        TypeName = f.TypeName,
        OffsetDisplay = $"0x{f.Offset:X4}",
        DisplayValue = f.DisplayValue ?? "",
        Color = colorIndex >= 0
          ? StructureHexView.FieldColors[colorIndex % StructureHexView.FieldColors.Length]
          : Color.Transparent,
        Children = f.Children is not null ? ToViewModels(f.Children, index) : null,
      });

      if (f.Children is not null && !hasOwnBytes) index += CountLeafFields(f.Children);
    }

    return result;
  }

  private static int CountLeafFields(List<ParsedField> fields) {
    var count = 0;
    foreach (var f in fields)
      if (f.Size > 0 || f.Children is not { Count: > 0 }) ++count;
      else count += CountLeafFields(f.Children);
    return count;
  }
}

/// <summary>One row of the parsed-structure tree.</summary>
internal sealed class StructureFieldViewModel {
  public required string Name { get; init; }
  public required string TypeName { get; init; }
  public required string OffsetDisplay { get; init; }
  public required string DisplayValue { get; init; }
  public required Color Color { get; init; }
  public List<StructureFieldViewModel>? Children { get; init; }
}
