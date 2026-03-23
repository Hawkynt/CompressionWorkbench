using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Compression.Analysis.Statistics;
using UserControl = System.Windows.Controls.UserControl;

namespace Compression.UI.Controls;

public partial class StringSearchControl : UserControl {
  private byte[]? _data;
  private List<StringExtractor.StringResult>? _allResults;

  public StringSearchControl() {
    InitializeComponent();
    MinLengthSlider.ValueChanged += (_, _) => MinLengthLabel.Text = ((int)MinLengthSlider.Value).ToString();
  }

  /// <summary>Sets the binary data to search within. Auto-extracts strings.</summary>
  public byte[]? Data {
    get => _data;
    set {
      _data = value;
      if (_data != null && _data.Length > 0)
        ExtractStrings();
    }
  }

  /// <summary>Fired when user double-clicks a result row. Provides (offset, length).</summary>
  public event Action<long, int>? ResultDoubleClicked;

  private void ExtractStrings() {
    if (_data == null || _data.Length == 0) return;
    var minLen = (int)MinLengthSlider.Value;
    var bridgeGap = BridgeGapsCheck.IsChecked == true ? 1 : 0;
    var encoding = GetSelectedEncoding();
    _allResults = encoding switch {
      "UTF-16 LE" => StringExtractor.ExtractUtf16Strings(_data, minLen, littleEndian: true),
      "UTF-16 BE" => StringExtractor.ExtractUtf16Strings(_data, minLen, littleEndian: false),
      "UTF-8" => StringExtractor.ExtractUtf8Strings(_data, minLen, bridgeGap),
      _ => StringExtractor.ExtractAsciiStrings(_data, minLen, bridgeGap),
    };
    ApplyFilter();
  }

  private string GetSelectedEncoding() =>
    (EncodingBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "ASCII";

  private void OnExtractStrings(object sender, RoutedEventArgs e) => ExtractStrings();

  private void OnEncodingChanged(object sender, SelectionChangedEventArgs e) {
    if (_data != null && _data.Length > 0)
      ExtractStrings();
  }

  private void OnQueryChanged(object sender, RoutedEventArgs e) => ApplyFilter();

  private void ApplyFilter() {
    if (_allResults == null) {
      ResultsGrid.ItemsSource = null;
      CountLabel.Text = "";
      return;
    }

    var query = QueryBox.Text?.Trim();
    if (string.IsNullOrEmpty(query)) {
      ResultsGrid.ItemsSource = _allResults;
      CountLabel.Text = $"{_allResults.Count} strings";
      return;
    }

    var caseSensitive = CaseCheck.IsChecked == true;
    var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    var terms = ParseQueryTerms(query);

    var filtered = _allResults.Where(r => MatchesAll(r.Text, terms, comparison)).ToList();
    ResultsGrid.ItemsSource = filtered;
    CountLabel.Text = $"{filtered.Count} / {_allResults.Count} strings";
  }

  /// <summary>
  /// Parses query into terms. Quoted strings are exact tokens, unquoted words split by space are AND terms.
  /// </summary>
  private static List<(string term, bool exact)> ParseQueryTerms(string query) {
    var terms = new List<(string, bool)>();
    var i = 0;
    while (i < query.Length) {
      if (query[i] == '"') {
        var end = query.IndexOf('"', i + 1);
        if (end < 0) end = query.Length;
        var t = query[(i + 1)..end];
        if (t.Length > 0) terms.Add((t, true));
        i = end + 1;
      }
      else if (query[i] == ' ') {
        i++;
      }
      else {
        var end = query.IndexOf(' ', i);
        if (end < 0) end = query.Length;
        var qEnd = query.IndexOf('"', i);
        if (qEnd >= 0 && qEnd < end) end = qEnd;
        var t = query[i..end];
        if (t.Length > 0) terms.Add((t, false));
        i = end;
      }
    }
    return terms;
  }

  private static bool MatchesAll(string text, List<(string term, bool exact)> terms, StringComparison comparison) {
    foreach (var (term, _) in terms) {
      // Both exact and contains terms use Contains — exact just preserves spacing from quotes
      if (!text.Contains(term, comparison))
        return false;
    }
    return true;
  }

  private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) {
    if (ResultsGrid.SelectedItem is StringExtractor.StringResult sr)
      ResultDoubleClicked?.Invoke(sr.Offset, sr.Length);
  }
}
