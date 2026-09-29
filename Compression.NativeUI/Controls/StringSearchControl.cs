using System.Drawing;
using Compression.Analysis.Statistics;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// Extracts printable strings from a buffer and filters them live. The filter takes space-separated
/// AND terms, with quoted runs kept together.
/// </summary>
internal sealed class StringSearchControl : Panel {
  private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly TrackBar _minLength = new() { Minimum = 4, Maximum = 32, Value = 6 };
  private readonly Label _minLengthLabel = new() { Text = "6" };
  private readonly CheckBox _bridgeGaps = new() { Text = "Bridge gaps", Checked = true };
  private readonly Button _reExtract = new() { Text = "Re-extract" };

  private readonly TextBox _query = new();
  private readonly CheckBox _caseSensitive = new() { Text = "Case sensitive" };
  private readonly Label _countLabel = new() { ForeColor = Color.Gray };

  private readonly DataGridView _results = new() { ReadOnly = true, ShowGridLines = true };
  private readonly ToolTip _toolTips = new();

  private byte[]? _data;
  private List<StringExtractor.StringResult>? _allResults;

  public StringSearchControl() {
    this._encoding.Items.AddRange(["ASCII", "UTF-8", "UTF-16 LE", "UTF-16 BE"]);
    this._encoding.SelectedIndex = 0;
    this._encoding.SelectedIndexChanged += (_, _) => this.ExtractStrings();

    this._minLength.ValueChanged += (_, _) => this._minLengthLabel.Text = this._minLength.Value.ToString();
    this._reExtract.Click += (_, _) => this.ExtractStrings();
    this._toolTips.SetToolTip(this._bridgeGaps,
      "Join strings separated by 1 non-printable byte (e.g. path/to/file across NUL)");

    this._query.TextChanged += (_, _) => this.ApplyFilter();
    this._toolTips.SetToolTip(this._query, "Space = AND, \"quoted\" = exact match");
    this._caseSensitive.CheckedChanged += (_, _) => this.ApplyFilter();

    this._results.Columns.Add(new DataGridViewColumn("Offset (hex)", static o => ((StringExtractor.StringResult)o!).Offset.ToString("X")) { Width = 90 });
    this._results.Columns.Add(new DataGridViewColumn("Offset (dec)", static o => ((StringExtractor.StringResult)o!).Offset) { Width = 90 });
    this._results.Columns.Add(new DataGridViewColumn("Length", static o => ((StringExtractor.StringResult)o!).Length) { Width = 60 });
    this._results.Columns.Add(new DataGridViewColumn("String", static o => ((StringExtractor.StringResult)o!).Text) { Width = 500 });
    this._results.CellDoubleClick += (_, _) => {
      if (this._results.SelectedItem is StringExtractor.StringResult sr)
        this.ResultDoubleClicked?.Invoke(sr.Offset, sr.Length);
    };

    this.Controls.AddRange(
      new Label { Text = "Encoding:" }, this._encoding,
      new Label { Text = "Min length:" }, this._minLength, this._minLengthLabel,
      this._bridgeGaps, this._reExtract,
      new Label { Text = "Filter:" }, this._query, this._caseSensitive, this._countLabel,
      this._results);

    // Control exposes no resize event, so the results grid follows the panel through anchoring
    // rather than a manual relayout pass.
    this._results.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
    this.LayoutChildren();
  }

  /// <summary>The buffer to search. Setting it extracts immediately.</summary>
  public byte[]? Data {
    get => this._data;
    set {
      this._data = value;
      if (value is { Length: > 0 }) this.ExtractStrings();
    }
  }

  /// <summary>Raised on a double-click, carrying the match's offset and length.</summary>
  public event Action<long, int>? ResultDoubleClicked;

  /// <summary>Places the two option rows; the grid fills whatever is left.</summary>
  private void LayoutChildren() {
    // Row 0: extraction options.
    var controls = this.Controls;
    controls[0].Bounds = new(0, 6, 60, 22);       // "Encoding:"
    this._encoding.Bounds = new(62, 4, 100, 24);
    controls[2].Bounds = new(170, 6, 66, 22);     // "Min length:"
    this._minLength.Bounds = new(238, 4, 100, 24);
    this._minLengthLabel.Bounds = new(342, 6, 24, 22);
    this._bridgeGaps.Bounds = new(372, 5, 100, 22);
    this._reExtract.Bounds = new(480, 4, 84, 24);

    // Row 1: filter.
    controls[7].Bounds = new(0, 36, 40, 22);      // "Filter:"
    this._query.Bounds = new(42, 34, 300, 24);
    this._caseSensitive.Bounds = new(350, 35, 110, 22);
    this._countLabel.Bounds = new(468, 36, 200, 22);

    this._results.Bounds = new(0, 64, Math.Max(0, this.Width), Math.Max(0, this.Height - 64));
  }

  private void ExtractStrings() {
    if (this._data is not { Length: > 0 } data) return;

    var minLength = this._minLength.Value;
    var bridgeGap = this._bridgeGaps.Checked ? 1 : 0;

    this._allResults = (this._encoding.SelectedItem as string) switch {
      "UTF-16 LE" => StringExtractor.ExtractUtf16Strings(data, minLength, littleEndian: true),
      "UTF-16 BE" => StringExtractor.ExtractUtf16Strings(data, minLength, littleEndian: false),
      "UTF-8" => StringExtractor.ExtractUtf8Strings(data, minLength, bridgeGap),
      _ => StringExtractor.ExtractAsciiStrings(data, minLength, bridgeGap),
    };

    this.ApplyFilter();
  }

  private void ApplyFilter() {
    if (this._allResults is not { } all) {
      this._results.DataSource = Array.Empty<object>();
      this._countLabel.Text = "";
      return;
    }

    var query = this._query.Text?.Trim();
    if (string.IsNullOrEmpty(query)) {
      this._results.DataSource = all;
      this._countLabel.Text = $"{all.Count} strings";
      return;
    }

    var comparison = this._caseSensitive.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    var terms = ParseQueryTerms(query);
    var filtered = all.Where(r => terms.All(t => r.Text.Contains(t, comparison))).ToList();

    this._results.DataSource = filtered;
    this._countLabel.Text = $"{filtered.Count} / {all.Count} strings";
  }

  /// <summary>
  /// Splits a query into AND terms. A quoted run is one term, preserving its spacing; everything
  /// else splits on whitespace.
  /// </summary>
  private static List<string> ParseQueryTerms(string query) {
    var terms = new List<string>();
    var i = 0;

    while (i < query.Length) {
      if (query[i] == '"') {
        var end = query.IndexOf('"', i + 1);
        if (end < 0) end = query.Length;
        var term = query[(i + 1)..end];
        if (term.Length > 0) terms.Add(term);
        i = end + 1;
        continue;
      }

      if (query[i] == ' ') {
        ++i;
        continue;
      }

      var space = query.IndexOf(' ', i);
      if (space < 0) space = query.Length;
      var quote = query.IndexOf('"', i);
      if (quote >= 0 && quote < space) space = quote;

      var word = query[i..space];
      if (word.Length > 0) terms.Add(word);
      i = space;
    }

    return terms;
  }
}
