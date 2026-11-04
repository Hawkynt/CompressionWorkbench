using System.Text;
using System.Xml;

namespace FileFormat.Qm;

/// <summary>A Qt Linguist TS message (only what reaches a QM file).</summary>
public sealed record TsMessage(string Context, string Source, string Comment, IReadOnlyList<string> Translations, bool Numerus, string Type, string? Id) {
  /// <summary>Set when the QM record this came from did not store its source text.</summary>
  public bool Stripped { get; init; }
}

/// <summary>
/// Qt Linguist source catalogs (<c>.ts</c> XML): parsed for compiling to QM, and written back from a
/// QM file. Only the elements that reach a QM file are kept: context names, source, disambiguation
/// comment, translations and plural forms (length variants joined by U+009C, as Qt does), the
/// translation type, the language and the dependencies.
/// </summary>
public sealed class TsDocument {

  /// <summary>Qt's separator between the length variants of one translation.</summary>
  public const char LengthVariantSeparator = '\u009C';

  public TsDocument(string? language, IReadOnlyList<string> dependencies, IReadOnlyList<TsMessage> messages) {
    this.Language = language;
    this.Dependencies = dependencies;
    this.Messages = messages;
  }

  public string? Language { get; }
  public IReadOnlyList<string> Dependencies { get; }
  public IReadOnlyList<TsMessage> Messages { get; }

  // ── parsing ──────────────────────────────────────────────────────────

  public static TsDocument Parse(byte[] xml) {
    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreProcessingInstructions = true, XmlResolver = null };
    try {
      using var reader = XmlReader.Create(new MemoryStream(xml), settings);
      var doc = new System.Xml.XmlDocument { PreserveWhitespace = true, XmlResolver = null };
      doc.Load(reader);
      var root = doc.DocumentElement;
      if (root is not { Name: "TS" })
        throw new InvalidDataException("Not a Qt TS file (no <TS> root element).");
      var language = root.GetAttribute("language");
      var dependencies = new List<string>();
      var messages = new List<TsMessage>();
      foreach (var node in root.ChildNodes.OfType<XmlElement>()) {
        if (node.Name == "dependencies") {
          dependencies.AddRange(node.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "dependency").Select(e => e.GetAttribute("catalog")));
          continue;
        }
        if (node.Name != "context") continue;
        var context = Text(node.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == "name"));
        foreach (var m in node.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "message"))
          messages.Add(ParseMessage(context, m));
      }
      return new TsDocument(language.Length > 0 ? language : null, dependencies, messages);
    } catch (XmlException ex) {
      throw new InvalidDataException("Malformed TS XML: " + ex.Message, ex);
    }
  }

  private static TsMessage ParseMessage(string context, XmlElement m) {
    var child = (string name) => m.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == name);
    var translation = child("translation");
    var numerus = m.GetAttribute("numerus") == "yes";
    var forms = new List<string>();
    if (translation != null) {
      if (numerus)
        forms.AddRange(translation.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "numerusform").Select(Variants));
      else
        forms.Add(Variants(translation));
    }
    var id = m.GetAttribute("id");
    return new TsMessage(context, Text(child("source")), Text(child("comment")), forms, numerus,
      translation?.GetAttribute("type") ?? "", id.Length > 0 ? id : null);
  }

  /// <summary>A translation or plural form: its text, or its length variants joined by U+009C.</summary>
  private static string Variants(XmlElement e)
    => e.GetAttribute("variants") == "yes"
      ? string.Join(LengthVariantSeparator, e.ChildNodes.OfType<XmlElement>().Where(v => v.Name == "lengthvariant").Select(Text))
      : Text(e);

  /// <summary>Element text with Qt's <c>&lt;byte value="x7"/&gt;</c> escapes for characters XML cannot hold.</summary>
  private static string Text(XmlElement? e) {
    if (e == null) return "";
    var sb = new StringBuilder();
    foreach (XmlNode n in e.ChildNodes)
      switch (n) {
        case XmlText or XmlWhitespace or XmlSignificantWhitespace or XmlCDataSection:
          sb.Append(n.Value);
          break;
        case XmlElement { Name: "byte" } b: {
          var v = b.GetAttribute("value");
          var code = v.StartsWith('x') ? Convert.ToInt32(v[1..], 16) : int.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
          sb.Append((char)code);
          break;
        }
      }
    return sb.ToString();
  }

  // ── compiling (what lrelease does in its default mode) ───────────────

  /// <summary>Compiles to a QM catalog, applying lrelease's message selection, comment dropping and plural padding.</summary>
  public QmCatalog Compile() {
    if (this.Messages.FirstOrDefault(m => m.Id != null) is { } idBased)
      throw new NotSupportedException($"TS message '{idBased.Source}' is id-based; compiling id-based catalogs is not supported.");
    var numerus = QmNumerus.For(this.Language);
    var forms = numerus?.Forms ?? 1;

    // lrelease first drops later duplicates of a (context, source, comment) triple, obsolete ones included.
    var seen = new HashSet<(string, string, string)>();
    var unique = this.Messages.Where(m => seen.Add((m.Context, m.Source, m.Comment))).ToList();

    var kept = new List<QmMessage>();
    var keys = new HashSet<(string, string, string)>();
    foreach (var m in unique) {
      if (m.Type is "obsolete" or "vanished") continue;
      if (m.Type == "unfinished" && (m.Translations.Count == 0 || m.Translations[0].Length == 0)) continue;
      // lrelease pads or truncates plural forms to the language's count; empty text is a null string.
      var count = m.Numerus ? forms : 1;
      var translations = Enumerable.Range(0, count)
        .Select(i => i < m.Translations.Count && m.Translations[i].Length > 0 ? m.Translations[i] : null)
        .ToList();
      var forceComment = m.Comment.Length == 0 || m.Context.Length == 0 ||
        unique.Any(o => o.Source == m.Source && o.Context == m.Context && o.Comment.Length == 0);
      var key = (m.Context, m.Source, "");
      if (forceComment || keys.Contains(key)) key = (m.Context, m.Source, m.Comment);
      if (!keys.Add(key)) continue;
      kept.Add(new QmMessage(key.Item1, key.Item2, key.Item3, translations));
    }
    var rules = numerus?.Rules ?? [];
    return new QmCatalog(this.Language, this.Dependencies, kept, rules);
  }

  // ── decompiling ──────────────────────────────────────────────────────

  /// <summary>
  /// A TS document holding a QM catalog's messages, grouped by context in file order. A plural message
  /// is one with more than one form, or — for a one-form language — one whose source contains <c>%n</c>;
  /// trailing null forms are left out because compiling pads them back.
  /// </summary>
  public static TsDocument FromCatalog(QmCatalog qm) {
    var oneForm = (QmNumerus.For(qm.Language)?.Forms ?? 1) == 1;
    var messages = qm.Messages.Select(m => {
      var numerus = m.Translations.Count > 1 || (oneForm && m.SourceText?.Contains("%n", StringComparison.Ordinal) == true);
      var forms = m.Translations.ToList();
      while (forms.Count > 0 && forms[^1] == null) forms.RemoveAt(forms.Count - 1);
      return new TsMessage(m.Context ?? "", m.SourceText ?? "", m.Comment ?? "", [.. forms.Select(f => f ?? "")], numerus, "",
        null) { Stripped = m.SourceText == null };
    }).ToList();
    return new TsDocument(qm.Language, qm.Dependencies, messages);
  }

  /// <summary>Serializes as TS 2.1 XML in Qt Linguist's layout.</summary>
  public byte[] ToXml() {
    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE TS>\n<TS version=\"2.1\"");
    if (this.Language != null) sb.Append(" language=\"").Append(Escape(this.Language)).Append('"');
    sb.Append(">\n");
    if (this.Dependencies.Count > 0) {
      sb.Append("<dependencies>\n");
      foreach (var d in this.Dependencies) sb.Append("    <dependency catalog=\"").Append(Escape(d)).Append("\"/>\n");
      sb.Append("</dependencies>\n");
    }
    foreach (var group in this.Messages.GroupBy(m => m.Context)) {
      sb.Append("<context>\n    <name>").Append(Escape(group.Key)).Append("</name>\n");
      foreach (var m in group) {
        sb.Append(m.Numerus ? "    <message numerus=\"yes\">\n" : "    <message>\n");
        if (m.Stripped) sb.Append("        <!-- source text not stored in this QM (lrelease -compress) -->\n");
        sb.Append("        <source>").Append(Escape(m.Source)).Append("</source>\n");
        if (m.Comment.Length > 0) sb.Append("        <comment>").Append(Escape(m.Comment)).Append("</comment>\n");
        if (m.Numerus) {
          sb.Append("        <translation>\n");
          foreach (var f in m.Translations) AppendTranslation(sb, "            ", "numerusform", f);
          sb.Append("        </translation>\n");
        } else {
          AppendTranslation(sb, "        ", "translation", m.Translations.Count > 0 ? m.Translations[0] : "");
        }
        sb.Append("    </message>\n");
      }
      sb.Append("</context>\n");
    }
    sb.Append("</TS>\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void AppendTranslation(StringBuilder sb, string indent, string element, string text) {
    if (!text.Contains(LengthVariantSeparator)) {
      sb.Append(indent).Append('<').Append(element).Append('>').Append(Escape(text)).Append("</").Append(element).Append(">\n");
      return;
    }
    sb.Append(indent).Append('<').Append(element).Append(" variants=\"yes\">\n");
    foreach (var v in text.Split(LengthVariantSeparator))
      sb.Append(indent).Append("    <lengthvariant>").Append(Escape(v)).Append("</lengthvariant>\n");
    sb.Append(indent).Append("</").Append(element).Append(">\n");
  }

  private static string Escape(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
      switch (c) {
        case '&': sb.Append("&amp;"); break;
        case '<': sb.Append("&lt;"); break;
        case '>': sb.Append("&gt;"); break;
        case '"': sb.Append("&quot;"); break;
        case '\t' or '\n': sb.Append(c); break;
        case '\r': sb.Append("&#xd;"); break;
        case < ' ' or '￾' or '￿': sb.Append("<byte value=\"x").Append(((int)c).ToString("x")).Append("\"/>"); break;
        default: sb.Append(c); break;
      }
    return sb.ToString();
  }
}
