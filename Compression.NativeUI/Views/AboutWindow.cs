using System.Drawing;
using System.Reflection;
using System.Text;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.NativeForms.Text;

namespace Compression.NativeUI.Views;

/// <summary>
/// Renders the project README as formatted text. The markdown is embedded at build time, so the
/// About box always describes the build the user is running.
/// </summary>
internal sealed class AboutWindow : Form {
  private const float BodySize = 10f;
  private const float CodeSize = 9f;

  private static readonly Color CodeColor = Color.FromArgb(0x8B, 0x1A, 0x1A);
  private static readonly Color LinkColor = Color.SteelBlue;
  private static readonly Color QuoteColor = Color.DimGray;
  private static readonly Color RuleColor = Color.LightGray;

  private readonly RichTextBox _document = new() { ReadOnly = true, Multiline = true, DetectUrls = true };
  private readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.OK };

  public AboutWindow() {
    this.Text = "About CompressionWorkbench";
    this.ClientSize = new(800, 640);
    this.MinimumSize = new(520, 400);
    this.StartPosition = FormStartPosition.CenterParent;

    this._close.Click += (_, _) => this.Close();
    this.AcceptButton = this._close;
    this.CancelButton = this._close;

    this.Controls.AddRange(this._document, this._close);
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();

    this.LoadReadme();
  }

  private void LayoutChildren() {
    this._document.Bounds = new(0, 0, this.ClientSize.Width, Math.Max(0, this.ClientSize.Height - 42));
    this._close.Bounds = new(this.ClientSize.Width - 96, this.ClientSize.Height - 34, 80, 26);
  }

  private void LoadReadme() {
    var markdown = ReadEmbeddedReadme();
    var document = markdown is null
      ? RichDocument.FromPlainText("README.md not found.")
      : MarkdownToRichDocument(markdown);

    this._document.Rtf = RtfSerializer.Write(document);
  }

  private static string? ReadEmbeddedReadme() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("README.md");
    if (stream is null) return null;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  /// <summary>
  /// Converts the subset of markdown the README uses. Rich text has no table model, so table rows
  /// are laid out as padded monospace columns instead — the shape survives even though the borders
  /// do not.
  /// </summary>
  private static RichDocument MarkdownToRichDocument(string markdown) {
    var doc = new RichDocument();
    var lines = markdown.Split('\n');

    var inCodeBlock = false;
    var codeLines = new List<string>();
    var tableRows = new List<string[]>();

    foreach (var rawLine in lines) {
      var line = rawLine.TrimEnd('\r');

      if (line.StartsWith("```", StringComparison.Ordinal)) {
        if (inCodeBlock) {
          foreach (var code in codeLines)
            Add(doc, new RichTextRun(code, FontStyle.Regular, CodeColor, CodeSize));
          codeLines.Clear();
          inCodeBlock = false;
        } else {
          FlushTable(doc, tableRows);
          inCodeBlock = true;
        }
        continue;
      }

      if (inCodeBlock) {
        codeLines.Add(line);
        continue;
      }

      if (line.StartsWith('|') && line.EndsWith('|')) {
        var cells = line.Split('|')
          .Skip(1).SkipLast(1)
          .Select(c => c.Trim())
          .ToArray();

        // Separator rows (|---|---|) only mark the header boundary.
        if (cells.All(c => c.All(ch => ch is '-' or ':' or ' '))) continue;
        tableRows.Add(cells);
        continue;
      }

      FlushTable(doc, tableRows);

      if (string.IsNullOrWhiteSpace(line)) continue;

      if (line is "---" or "***" or "___") {
        Add(doc, new RichTextRun(new string('─', 60), FontStyle.Regular, RuleColor, BodySize));
        continue;
      }

      if (line.StartsWith('#')) {
        var level = line.TakeWhile(c => c == '#').Count();
        var size = level switch { 1 => 17f, 2 => 14f, 3 => 12f, _ => BodySize };
        Add(doc, new RichTextRun(line[level..].TrimStart(), FontStyle.Bold, Color.Black, size));
        continue;
      }

      if (line.StartsWith('>')) {
        var paragraph = new RichParagraph();
        AddInlines(paragraph, line[1..].TrimStart(), FontStyle.Italic, QuoteColor);
        doc.Paragraphs.Add(paragraph);
        continue;
      }

      if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) {
        var paragraph = new RichParagraph { Bullet = true };
        AddInlines(paragraph, line[2..], FontStyle.Regular, Color.Black);
        doc.Paragraphs.Add(paragraph);
        continue;
      }

      var dotIndex = line.IndexOf(". ", StringComparison.Ordinal);
      if (line.Length > 2 && char.IsDigit(line[0]) && dotIndex is >= 0 and <= 3) {
        var paragraph = new RichParagraph();
        paragraph.Runs.Add(new(line[..(dotIndex + 2)], FontStyle.Regular, Color.Black, BodySize));
        AddInlines(paragraph, line[(dotIndex + 2)..], FontStyle.Regular, Color.Black);
        doc.Paragraphs.Add(paragraph);
        continue;
      }

      var body = new RichParagraph();
      AddInlines(body, line, FontStyle.Regular, Color.Black);
      doc.Paragraphs.Add(body);
    }

    FlushTable(doc, tableRows);
    return doc;
  }

  private static void Add(RichDocument doc, RichTextRun run) {
    var paragraph = new RichParagraph();
    paragraph.Runs.Add(run);
    doc.Paragraphs.Add(paragraph);
  }

  private static void FlushTable(RichDocument doc, List<string[]> rows) {
    if (rows.Count == 0) return;

    var columns = rows.Max(r => r.Length);
    var widths = new int[columns];
    foreach (var row in rows)
      for (var c = 0; c < row.Length; ++c)
        widths[c] = Math.Max(widths[c], row[c].Length);

    for (var r = 0; r < rows.Count; ++r) {
      var sb = new StringBuilder();
      for (var c = 0; c < columns; ++c) {
        if (c > 0) sb.Append("  ");
        sb.Append((c < rows[r].Length ? rows[r][c] : "").PadRight(widths[c]));
      }
      Add(doc, new(sb.ToString().TrimEnd(), r == 0 ? FontStyle.Bold : FontStyle.Regular, Color.Black, CodeSize));
    }

    rows.Clear();
  }

  /// <summary>Splits a line into runs for inline code, bold, links and images.</summary>
  private static void AddInlines(RichParagraph paragraph, string text, FontStyle baseStyle, Color baseColor) {
    var i = 0;
    while (i < text.Length) {
      if (text[i] == '`') {
        var end = text.IndexOf('`', i + 1);
        if (end > i) {
          paragraph.Runs.Add(new(text[(i + 1)..end], baseStyle, CodeColor, CodeSize));
          i = end + 1;
          continue;
        }
      }

      if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*') {
        var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
        if (end > i) {
          paragraph.Runs.Add(new(text[(i + 2)..end], baseStyle | FontStyle.Bold, baseColor, BodySize));
          i = end + 2;
          continue;
        }
      }

      // Images are dropped; links keep their label.
      if (i + 1 < text.Length && text[i] == '!' && text[i + 1] == '[' && TryReadLink(text, i + 1, out _, out var imageEnd)) {
        i = imageEnd;
        continue;
      }

      if (text[i] == '[' && TryReadLink(text, i, out var label, out var linkEnd)) {
        paragraph.Runs.Add(new(label, baseStyle, LinkColor, BodySize));
        i = linkEnd;
        continue;
      }

      var nextSpecial = text.Length;
      for (var j = i + 1; j < text.Length; ++j)
        if (text[j] is '`' or '*' or '[' or '!') {
          nextSpecial = j;
          break;
        }

      paragraph.Runs.Add(new(text[i..nextSpecial], baseStyle, baseColor, BodySize));
      i = nextSpecial;
    }
  }

  private static bool TryReadLink(string text, int start, out string label, out int end) {
    label = "";
    end = start;

    var closeBracket = text.IndexOf(']', start + 1);
    if (closeBracket <= start || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(') return false;

    var closeParen = text.IndexOf(')', closeBracket + 2);
    if (closeParen <= closeBracket) return false;

    label = text[(start + 1)..closeBracket];
    end = closeParen + 1;
    return true;
  }
}
