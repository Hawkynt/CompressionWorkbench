using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Compression.UI.Views;

public partial class AboutWindow : Window {

  public AboutWindow() {
    InitializeComponent();
    LoadReadme();
  }

  private void LoadReadme() {
    var md = ReadEmbeddedReadme();
    if (md == null) {
      var doc = new FlowDocument(new Paragraph(new Run("README.md not found.")));
      DocViewer.Document = doc;
      return;
    }
    DocViewer.Document = MarkdownToFlowDocument(md);
  }

  private static string? ReadEmbeddedReadme() {
    var asm = Assembly.GetExecutingAssembly();
    using var stream = asm.GetManifestResourceStream("README.md");
    if (stream == null) return null;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  private static FlowDocument MarkdownToFlowDocument(string markdown) {
    var doc = new FlowDocument {
      FontFamily = new FontFamily("Segoe UI,sans-serif"),
      FontSize = 13,
      PagePadding = new Thickness(16),
    };

    var monoFont = new FontFamily("Cascadia Mono,Consolas,Courier New");
    var lines = markdown.Split('\n');
    var inCodeBlock = false;
    var codeLines = new List<string>();
    var inTable = false;
    var tableRows = new List<string[]>();

    for (var i = 0; i < lines.Length; i++) {
      var line = lines[i].TrimEnd('\r');

      // Code block toggle
      if (line.StartsWith("```")) {
        if (inCodeBlock) {
          // End code block
          var codePara = new Paragraph {
            FontFamily = monoFont,
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 4),
          };
          codePara.Inlines.Add(new Run(string.Join("\n", codeLines)));
          doc.Blocks.Add(codePara);
          codeLines.Clear();
          inCodeBlock = false;
        }
        else {
          FlushTable(doc, ref inTable, tableRows);
          inCodeBlock = true;
        }
        continue;
      }

      if (inCodeBlock) {
        codeLines.Add(line);
        continue;
      }

      // Table rows
      if (line.StartsWith('|') && line.EndsWith('|')) {
        var cells = line.Split('|', StringSplitOptions.None)
          .Skip(1).SkipLast(1)
          .Select(c => c.Trim())
          .ToArray();
        // Skip separator rows (|---|---|)
        if (cells.All(c => c.All(ch => ch == '-' || ch == ':' || ch == ' '))) {
          inTable = true;
          continue;
        }
        if (!inTable) inTable = true;
        tableRows.Add(cells);
        continue;
      }

      FlushTable(doc, ref inTable, tableRows);

      // Empty line
      if (string.IsNullOrWhiteSpace(line)) continue;

      // Horizontal rule
      if (line is "---" or "***" or "___") {
        doc.Blocks.Add(new Paragraph { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 8, 0, 8) });
        continue;
      }

      // Heading
      if (line.StartsWith('#')) {
        var level = line.TakeWhile(c => c == '#').Count();
        var text = line[level..].TrimStart();
        var para = new Paragraph(new Run(text)) {
          FontWeight = FontWeights.Bold,
          FontSize = level switch {
            1 => 22,
            2 => 18,
            3 => 15,
            _ => 13,
          },
          Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 4),
        };
        doc.Blocks.Add(para);
        continue;
      }

      // Blockquote
      if (line.StartsWith('>')) {
        var text = line[1..].TrimStart();
        var para = new Paragraph {
          BorderBrush = Brushes.Gray,
          BorderThickness = new Thickness(3, 0, 0, 0),
          Padding = new Thickness(8, 4, 4, 4),
          Foreground = Brushes.DimGray,
          FontStyle = FontStyles.Italic,
        };
        AddInlines(para.Inlines, text, monoFont);
        doc.Blocks.Add(para);
        continue;
      }

      // List item
      if (line.StartsWith("- ") || line.StartsWith("* ")) {
        var text = line[2..];
        var indent = 0;
        var trimmed = lines[i].TrimEnd('\r');
        while (indent < trimmed.Length && trimmed[indent] == ' ') indent++;
        var para = new Paragraph {
          Margin = new Thickness(12 + indent * 4, 1, 0, 1),
          TextIndent = -12,
        };
        para.Inlines.Add(new Run("\u2022 "));
        AddInlines(para.Inlines, text, monoFont);
        doc.Blocks.Add(para);
        continue;
      }

      // Numbered list item
      if (line.Length > 2 && char.IsDigit(line[0]) && line.Contains(". ")) {
        var dotIdx = line.IndexOf(". ", StringComparison.Ordinal);
        if (dotIdx >= 0 && dotIdx <= 3) {
          var num = line[..(dotIdx + 2)];
          var text = line[(dotIdx + 2)..];
          var para = new Paragraph { Margin = new Thickness(12, 1, 0, 1), TextIndent = -12 };
          para.Inlines.Add(new Run(num));
          AddInlines(para.Inlines, text, monoFont);
          doc.Blocks.Add(para);
          continue;
        }
      }

      // Normal paragraph
      {
        var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
        AddInlines(para.Inlines, line, monoFont);
        doc.Blocks.Add(para);
      }
    }

    FlushTable(doc, ref inTable, tableRows);
    return doc;
  }

  private static void FlushTable(FlowDocument doc, ref bool inTable, List<string[]> tableRows) {
    if (!inTable || tableRows.Count == 0) return;

    var table = new Table { CellSpacing = 0, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) };
    var colCount = tableRows.Max(r => r.Length);
    for (var c = 0; c < colCount; c++)
      table.Columns.Add(new TableColumn());

    var rg = new TableRowGroup();
    for (var r = 0; r < tableRows.Count; r++) {
      var row = new TableRow();
      if (r == 0) row.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
      for (var c = 0; c < colCount; c++) {
        var text = c < tableRows[r].Length ? tableRows[r][c] : "";
        var cell = new TableCell(new Paragraph(new Run(text)) { Margin = new Thickness(4, 2, 4, 2) }) {
          BorderBrush = Brushes.LightGray,
          BorderThickness = new Thickness(0, 0, 1, 1),
        };
        if (r == 0) cell.FontWeight = FontWeights.SemiBold;
        row.Cells.Add(cell);
      }
      rg.Rows.Add(row);
    }

    table.RowGroups.Add(rg);
    doc.Blocks.Add(table);
    tableRows.Clear();
    inTable = false;
  }

  private static void AddInlines(InlineCollection inlines, string text, FontFamily monoFont) {
    var i = 0;
    while (i < text.Length) {
      // Inline code
      if (text[i] == '`') {
        var end = text.IndexOf('`', i + 1);
        if (end > i) {
          var code = text[(i + 1)..end];
          inlines.Add(new Run(code) {
            FontFamily = monoFont,
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
          });
          i = end + 1;
          continue;
        }
      }

      // Bold (**text**)
      if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*') {
        var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
        if (end > i) {
          inlines.Add(new Bold(new Run(text[(i + 2)..end])));
          i = end + 2;
          continue;
        }
      }

      // Link [text](url) — render as plain text
      if (text[i] == '[') {
        var closeBracket = text.IndexOf(']', i + 1);
        if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(') {
          var closeParen = text.IndexOf(')', closeBracket + 2);
          if (closeParen > closeBracket) {
            var linkText = text[(i + 1)..closeBracket];
            inlines.Add(new Run(linkText) { Foreground = Brushes.SteelBlue });
            i = closeParen + 1;
            continue;
          }
        }
      }

      // Image ![alt](url) — skip
      if (i + 1 < text.Length && text[i] == '!' && text[i + 1] == '[') {
        var closeBracket = text.IndexOf(']', i + 2);
        if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(') {
          var closeParen = text.IndexOf(')', closeBracket + 2);
          if (closeParen > closeBracket) {
            i = closeParen + 1;
            continue;
          }
        }
      }

      // Plain text — collect until next special char
      var nextSpecial = text.Length;
      for (var j = i + 1; j < text.Length; j++) {
        if (text[j] is '`' or '*' or '[' or '!') { nextSpecial = j; break; }
      }
      inlines.Add(new Run(text[i..nextSpecial]));
      i = nextSpecial;
    }
  }

  private void OnClose(object sender, RoutedEventArgs e) => Close();
}
