using System.Globalization;
using System.Text;

namespace FileFormat.Aff4;

/// <summary>An RDF term: an IRI, a blank node, or a literal with an optional datatype IRI.</summary>
internal readonly record struct Aff4RdfNode(Aff4RdfNodeKind Kind, string Value, string? Datatype = null) {
  public bool IsIri => this.Kind == Aff4RdfNodeKind.Iri;
}

internal enum Aff4RdfNodeKind { Iri, Blank, Literal }

internal readonly record struct Aff4Triple(Aff4RdfNode Subject, string Predicate, Aff4RdfNode Object);

/// <summary>
/// A Turtle reader for the AFF4 metadata graph (<c>information.turtle</c>), following the W3C
/// RDF 1.1 Turtle recommendation: <c>@prefix</c>/<c>PREFIX</c>, <c>@base</c>/<c>BASE</c>, IRIs,
/// prefixed names, the <c>a</c> keyword, short and long string literals with escapes, language
/// tags and datatypes, numbers and booleans, blank-node labels, property lists <c>[ ]</c> and
/// collections <c>( )</c>, and <c>;</c>/<c>,</c> lists. It is lenient where producers are known
/// to deviate: an unknown string escape keeps its backslash (the AFF4-L draft's own example writes
/// <c>"\GovDocs\000\000785.html"</c>), and a syntax error ends the parse with the triples read so
/// far instead of throwing.
/// </summary>
internal sealed class Aff4Turtle {
  private const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
  private const string XsdNs = "http://www.w3.org/2001/XMLSchema#";
  public const string RdfType = RdfNs + "type";

  private readonly string _text;
  private readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);
  private readonly List<Aff4Triple> _triples = [];
  private string _base = string.Empty;
  private int _pos;
  private int _blankCounter;

  private Aff4Turtle(string text) => this._text = text;

  /// <summary>Parses <paramref name="text"/>; never throws, returns what could be read.</summary>
  public static List<Aff4Triple> Parse(string text) {
    var parser = new Aff4Turtle(text);
    try {
      parser.ParseDocument();
    } catch (FormatException) {
      // Malformed tail: keep the statements that parsed.
    }
    return parser._triples;
  }

  private void ParseDocument() {
    while (true) {
      this.SkipWs();
      if (this._pos >= this._text.Length) return;
      if (this.TryKeyword("@prefix")) { this.ParsePrefix(); this.Expect('.'); continue; }
      if (this.TryKeyword("@base")) { this._base = this.ParseIriRef(); this.Expect('.'); continue; }
      if (this.TryKeywordInsensitive("PREFIX")) { this.ParsePrefix(); continue; }
      if (this.TryKeywordInsensitive("BASE")) { this._base = this.ParseIriRef(); continue; }
      this.ParseTriples();
      this.Expect('.');
    }
  }

  private void ParsePrefix() {
    this.SkipWs();
    var start = this._pos;
    while (this._pos < this._text.Length && this._text[this._pos] != ':') {
      if (char.IsWhiteSpace(this._text[this._pos])) throw new FormatException("prefix name");
      ++this._pos;
    }
    var name = this._text[start..this._pos];
    this.Expect(':');
    this._prefixes[name] = this.ParseIriRef();
  }

  private void ParseTriples() {
    this.SkipWs();
    Aff4RdfNode subject;
    if (this.Peek() == '[') {
      subject = this.ParseBlankPropertyList();
      this.SkipWs();
      if (this.Peek() == '.') return; // a bare "[ ... ] ." statement
    } else
      subject = this.ParseSubject();
    this.ParsePredicateObjectList(subject);
  }

  private void ParsePredicateObjectList(Aff4RdfNode subject) {
    while (true) {
      this.SkipWs();
      var predicate = this.ParseVerb();
      this.ParseObjectList(subject, predicate);
      this.SkipWs();
      if (this.Peek() != ';') return;
      while (this.Peek() == ';') { ++this._pos; this.SkipWs(); }
      if (this.Peek() is '.' or ']' or '\0') return;
    }
  }

  private void ParseObjectList(Aff4RdfNode subject, string predicate) {
    while (true) {
      this._triples.Add(new Aff4Triple(subject, predicate, this.ParseObject()));
      this.SkipWs();
      if (this.Peek() != ',') return;
      ++this._pos;
    }
  }

  private string ParseVerb() {
    this.SkipWs();
    if (this.Peek() == 'a' && this._pos + 1 < this._text.Length && (char.IsWhiteSpace(this._text[this._pos + 1]) || this._text[this._pos + 1] is '<' or '"' or '[' or '_')) {
      ++this._pos;
      return RdfType;
    }
    var node = this.ParseIriOrPrefixed();
    return node.Value;
  }

  private Aff4RdfNode ParseSubject() {
    this.SkipWs();
    return this.Peek() switch {
      '_' => this.ParseBlankLabel(),
      '(' => this.ParseCollection(),
      _ => this.ParseIriOrPrefixed(),
    };
  }

  private Aff4RdfNode ParseObject() {
    this.SkipWs();
    var c = this.Peek();
    switch (c) {
      case '"' or '\'': return this.ParseLiteral();
      case '[': return this.ParseBlankPropertyList();
      case '(': return this.ParseCollection();
      case '_': return this.ParseBlankLabel();
      case '<': return this.ParseIriOrPrefixed();
    }
    if (c is '+' or '-' or '.' || char.IsAsciiDigit(c)) return this.ParseNumber();
    if (this.TryKeyword("true")) return new(Aff4RdfNodeKind.Literal, "true", XsdNs + "boolean");
    if (this.TryKeyword("false")) return new(Aff4RdfNodeKind.Literal, "false", XsdNs + "boolean");
    return this.ParseIriOrPrefixed();
  }

  private Aff4RdfNode ParseBlankPropertyList() {
    this.Expect('[');
    var node = new Aff4RdfNode(Aff4RdfNodeKind.Blank, "b" + (++this._blankCounter).ToString(CultureInfo.InvariantCulture));
    this.SkipWs();
    if (this.Peek() != ']') this.ParsePredicateObjectList(node);
    this.Expect(']');
    return node;
  }

  private Aff4RdfNode ParseCollection() {
    this.Expect('(');
    var items = new List<Aff4RdfNode>();
    while (true) {
      this.SkipWs();
      if (this.Peek() == ')') { ++this._pos; break; }
      if (this.Peek() == '\0') throw new FormatException("collection");
      items.Add(this.ParseObject());
    }
    var head = new Aff4RdfNode(Aff4RdfNodeKind.Iri, RdfNs + "nil");
    for (var i = items.Count - 1; i >= 0; --i) {
      var cell = new Aff4RdfNode(Aff4RdfNodeKind.Blank, "b" + (++this._blankCounter).ToString(CultureInfo.InvariantCulture));
      this._triples.Add(new(cell, RdfNs + "first", items[i]));
      this._triples.Add(new(cell, RdfNs + "rest", head));
      head = cell;
    }
    return head;
  }

  private Aff4RdfNode ParseBlankLabel() {
    this.Expect('_');
    this.Expect(':');
    var start = this._pos;
    while (this._pos < this._text.Length && IsNameChar(this._text[this._pos])) ++this._pos;
    while (this._pos > start && this._text[this._pos - 1] == '.') --this._pos;
    return new(Aff4RdfNodeKind.Blank, "_" + this._text[start..this._pos]);
  }

  private Aff4RdfNode ParseIriOrPrefixed() {
    this.SkipWs();
    if (this.Peek() == '<') return new(Aff4RdfNodeKind.Iri, this.ParseIriRef());
    var start = this._pos;
    while (this._pos < this._text.Length && this._text[this._pos] != ':' && IsNameChar(this._text[this._pos])) ++this._pos;
    if (this.Peek() != ':') throw new FormatException("prefixed name");
    var prefix = this._text[start..this._pos];
    ++this._pos;
    var local = new StringBuilder();
    while (this._pos < this._text.Length) {
      var c = this._text[this._pos];
      if (c == '\\' && this._pos + 1 < this._text.Length) { local.Append(this._text[this._pos + 1]); this._pos += 2; continue; }
      if (c == '%' || IsNameChar(c) || c == ':') { local.Append(c); ++this._pos; continue; }
      break;
    }
    // A trailing '.' ends the statement rather than belonging to the local name.
    while (local.Length > 0 && local[^1] == '.') { local.Length--; --this._pos; }
    if (!this._prefixes.TryGetValue(prefix, out var ns)) throw new FormatException($"undeclared prefix '{prefix}'");
    return new(Aff4RdfNodeKind.Iri, ns + local);
  }

  private string ParseIriRef() {
    this.SkipWs();
    this.Expect('<');
    var sb = new StringBuilder();
    while (true) {
      if (this._pos >= this._text.Length) throw new FormatException("unterminated IRI");
      var c = this._text[this._pos++];
      if (c == '>') break;
      if (c == '\\' && this._pos < this._text.Length && this._text[this._pos] is 'u' or 'U') {
        sb.Append(this.ReadUnicodeEscape(this._text[this._pos++] == 'u' ? 4 : 8));
        continue;
      }
      sb.Append(c);
    }
    var iri = sb.ToString();
    return iri.Contains(':', StringComparison.Ordinal) || this._base.Length == 0 ? iri : this._base + iri;
  }

  private Aff4RdfNode ParseLiteral() {
    var quote = this._text[this._pos];
    var isLong = this._pos + 2 < this._text.Length && this._text[this._pos + 1] == quote && this._text[this._pos + 2] == quote;
    this._pos += isLong ? 3 : 1;
    var sb = new StringBuilder();
    while (true) {
      if (this._pos >= this._text.Length) throw new FormatException("unterminated string");
      var c = this._text[this._pos];
      if (isLong) {
        if (c == quote && this._pos + 2 < this._text.Length && this._text[this._pos + 1] == quote && this._text[this._pos + 2] == quote) {
          this._pos += 3;
          // """a"""" : extra closing quotes belong to the content.
          while (this.Peek() == quote) { sb.Append(quote); ++this._pos; }
          break;
        }
      } else if (c == quote) { ++this._pos; break; }
      else if (c is '\n' or '\r') throw new FormatException("newline in short string");
      if (c == '\\' && this._pos + 1 < this._text.Length) {
        var e = this._text[this._pos + 1];
        this._pos += 2;
        switch (e) {
          case 't': sb.Append('\t'); break;
          case 'b': sb.Append('\b'); break;
          case 'n': sb.Append('\n'); break;
          case 'r': sb.Append('\r'); break;
          case 'f': sb.Append('\f'); break;
          case '"': sb.Append('"'); break;
          case '\'': sb.Append('\''); break;
          case '\\': sb.Append('\\'); break;
          case 'u': sb.Append(this.ReadUnicodeEscape(4)); break;
          case 'U': sb.Append(this.ReadUnicodeEscape(8)); break;
          default: sb.Append('\\').Append(e); break; // not Turtle, but written by producers
        }
        continue;
      }
      sb.Append(c);
      ++this._pos;
    }
    string? datatype = null;
    if (this.Peek() == '@') {
      ++this._pos;
      while (this._pos < this._text.Length && (char.IsAsciiLetterOrDigit(this._text[this._pos]) || this._text[this._pos] == '-')) ++this._pos;
    } else if (this.Peek() == '^' && this._pos + 1 < this._text.Length && this._text[this._pos + 1] == '^') {
      this._pos += 2;
      datatype = this.ParseIriOrPrefixed().Value;
    }
    return new(Aff4RdfNodeKind.Literal, sb.ToString(), datatype);
  }

  private Aff4RdfNode ParseNumber() {
    var start = this._pos;
    if (this.Peek() is '+' or '-') ++this._pos;
    var isDecimal = false;
    var isDouble = false;
    while (this._pos < this._text.Length) {
      var c = this._text[this._pos];
      if (char.IsAsciiDigit(c)) { ++this._pos; continue; }
      if (c == '.' && this._pos + 1 < this._text.Length && char.IsAsciiDigit(this._text[this._pos + 1])) { isDecimal = true; ++this._pos; continue; }
      if (c is 'e' or 'E') {
        isDouble = true;
        ++this._pos;
        if (this.Peek() is '+' or '-') ++this._pos;
        continue;
      }
      break;
    }
    if (this._pos == start) throw new FormatException("number");
    var type = isDouble ? "double" : isDecimal ? "decimal" : "integer";
    return new(Aff4RdfNodeKind.Literal, this._text[start..this._pos], XsdNs + type);
  }

  private string ReadUnicodeEscape(int digits) {
    if (this._pos + digits > this._text.Length) throw new FormatException("escape");
    var hex = this._text.AsSpan(this._pos, digits);
    this._pos += digits;
    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) || code is < 0 or > 0x10FFFF)
      throw new FormatException("escape");
    return char.ConvertFromUtf32(code is >= 0xD800 and <= 0xDFFF ? 0xFFFD : code);
  }

  private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' || c > 0x7F;

  private char Peek() => this._pos < this._text.Length ? this._text[this._pos] : '\0';

  private void Expect(char c) {
    this.SkipWs();
    if (this.Peek() != c) throw new FormatException($"expected '{c}'");
    ++this._pos;
  }

  private bool TryKeyword(string keyword) {
    if (string.CompareOrdinal(this._text, this._pos, keyword, 0, keyword.Length) != 0) return false;
    var end = this._pos + keyword.Length;
    if (end < this._text.Length && IsNameChar(this._text[end]) && this._text[end] != '.') return false;
    this._pos = end;
    return true;
  }

  private bool TryKeywordInsensitive(string keyword) {
    if (string.Compare(this._text, this._pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
    var end = this._pos + keyword.Length;
    if (end >= this._text.Length || !char.IsWhiteSpace(this._text[end])) return false;
    this._pos = end;
    return true;
  }

  private void SkipWs() {
    while (this._pos < this._text.Length) {
      var c = this._text[this._pos];
      if (char.IsWhiteSpace(c) || c == (char)0xFEFF) { ++this._pos; continue; }
      if (c == '#') {
        while (this._pos < this._text.Length && this._text[this._pos] is not ('\n' or '\r')) ++this._pos;
        continue;
      }
      break;
    }
  }
}
