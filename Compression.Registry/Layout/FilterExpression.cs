using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Compression.Registry.Layout;

/// <summary>
/// Parses a tiny predicate language into an <see cref="IFileFilter"/>.
///
/// <para>Grammar (case-insensitive identifiers, whitespace skipped between tokens):</para>
/// <code>
/// expr        := orExpr
/// orExpr      := andExpr ( "or" andExpr )*
/// andExpr     := primary ( "and" primary )*
/// primary     := "(" expr ")" | "not" primary | comparison
/// comparison  := field op value
/// field       := identifier  (name | path | extension | size | lastModified | lastAccessed | created | attributes)
/// op          := "=" | "==" | "!=" | "&lt;" | "&lt;=" | "&gt;" | "&gt;=" | "contains" | "matches" | "in"
/// value       := term ( ("+" | "-") term )*
/// term        := number | sizeLiteral | string | function | "true" | "false" | "null" | "(" value ")"
/// function    := identifier "(" arglist? ")"
/// </code>
/// <para>Functions:</para>
/// <list type="bullet">
///   <item><c>quartile(p)</c> — p-th percentile (0..1) of the file set, resolved dynamically per the field on the LHS.</item>
///   <item><c>now()</c> — current UTC time.</item>
///   <item><c>today()</c> — current UTC date at midnight.</item>
///   <item><c>days(n)</c> / <c>hours(n)</c> / <c>minutes(n)</c> — durations (subtract from now() / today()).</item>
///   <item><c>date("yyyy-MM-dd")</c> — explicit literal.</item>
/// </list>
/// <para>Error reporting: parse errors include the source offset of the
/// failing token, e.g. <c>"unknown field 'foobar' at position 7"</c>.</para>
/// </summary>
public static class FilterExpression {

  private static readonly ConcurrentDictionary<string, IFileFilter> _cache = new(StringComparer.Ordinal);

  /// <summary>
  /// Parses <paramref name="expression"/> into an <see cref="IFileFilter"/>.
  /// Compiled filters are cached by string identity — passing the same
  /// string twice returns the same filter instance.
  /// </summary>
  /// <exception cref="FormatException">Thrown when the expression is malformed.
  /// The message includes the source position of the failing token.</exception>
  public static IFileFilter Parse(string expression) {
    ArgumentNullException.ThrowIfNull(expression);
    return _cache.GetOrAdd(expression, static src => {
      var parser = new Parser(src);
      var node = parser.ParseExpr();
      parser.ExpectEof();
      return new CompiledFilter(node);
    });
  }

  /// <summary>Resets the internal parse cache. Test-only.</summary>
  internal static void ClearCache() => _cache.Clear();

  // ── AST ────────────────────────────────────────────────────────────────

  internal abstract record Node;

  internal sealed record AndNode(Node Left, Node Right) : Node;
  internal sealed record OrNode(Node Left, Node Right) : Node;
  internal sealed record NotNode(Node Inner) : Node;

  internal sealed record CompareNode(DefragSortField Field, CompareOp Op, ValueNode Rhs) : Node;
  internal sealed record InNode(DefragSortField Field, IReadOnlyList<ValueNode> Values) : Node;
  internal sealed record ContainsNode(DefragSortField Field, string Needle) : Node;
  internal sealed record MatchesNode(DefragSortField Field, Regex Pattern) : Node;

  internal enum CompareOp { Eq, Ne, Lt, Le, Gt, Ge }

  internal abstract record ValueNode {
    public abstract object? Evaluate(IFilterFileContext file, DefragSortField field);
  }

  internal sealed record NumberValue(double Value) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => this.Value;
  }

  internal sealed record SizeValue(long Bytes) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => (double)this.Bytes;
  }

  internal sealed record DurationValue(TimeSpan Span) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => this.Span;
  }

  internal sealed record StringValue(string Text) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => this.Text;
  }

  internal sealed record BoolValue(bool Value) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => this.Value;
  }

  internal sealed record NullValue : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => null;
  }

  internal sealed record DateValue(DateTime Value) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => this.Value;
  }

  internal sealed record BinaryValue(ValueNode Left, char Op, ValueNode Right) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) {
      var l = this.Left.Evaluate(file, field);
      var r = this.Right.Evaluate(file, field);
      if (l is DateTime dl && r is TimeSpan ts) return this.Op == '+' ? dl + ts : dl - ts;
      if (l is DateTime dlt && r is DateTime drt && this.Op == '-') return drt - dlt;
      if (l is double ld && r is double rd) return this.Op == '+' ? ld + rd : ld - rd;
      if (l is TimeSpan tsl && r is TimeSpan tsr) return this.Op == '+' ? tsl + tsr : tsl - tsr;
      throw new InvalidOperationException(
        $"Cannot apply '{this.Op}' to {l?.GetType().Name ?? "null"} and {r?.GetType().Name ?? "null"}.");
    }
  }

  internal sealed record QuartileValue(double Percentile) : ValueNode {
    public override object? Evaluate(IFilterFileContext file, DefragSortField field) => field switch {
      DefragSortField.Size => ComputeNumericPercentile(file.AllSizes, this.Percentile),
      DefragSortField.LastModified => ComputeDatePercentile(file.AllLastModifiedTimes, this.Percentile),
      DefragSortField.LastAccessed => ComputeDatePercentile(file.AllLastAccessedTimes, this.Percentile),
      DefragSortField.Created => ComputeDatePercentile(file.AllCreatedTimes, this.Percentile),
      _ => throw new InvalidOperationException(
        $"quartile() is only defined for numeric / date fields, not {field}."),
    };

    private static object? ComputeNumericPercentile(IReadOnlyList<long>? values, double p) {
      if (values is null || values.Count == 0) return null;
      var sorted = values.OrderBy(v => v).ToArray();
      var idx = (int)Math.Clamp(p * (sorted.Length - 1), 0, sorted.Length - 1);
      return (double)sorted[idx];
    }

    private static object? ComputeDatePercentile(IReadOnlyList<DateTime>? values, double p) {
      if (values is null || values.Count == 0) return null;
      var sorted = values.OrderBy(v => v).ToArray();
      var idx = (int)Math.Clamp(p * (sorted.Length - 1), 0, sorted.Length - 1);
      return sorted[idx];
    }
  }

  // ── Compiled filter wrapper ────────────────────────────────────────────

  private sealed class CompiledFilter(Node root) : IFileFilter {
    public bool Matches(IFilterFileContext file) => Evaluator.Eval(root, file);
  }

  // ── Evaluator ──────────────────────────────────────────────────────────

  private static class Evaluator {
    public static bool Eval(Node node, IFilterFileContext file) => node switch {
      AndNode a => Eval(a.Left, file) && Eval(a.Right, file),
      OrNode o => Eval(o.Left, file) || Eval(o.Right, file),
      NotNode n => !Eval(n.Inner, file),
      CompareNode c => EvalCompare(c, file),
      InNode i => EvalIn(i, file),
      ContainsNode co => EvalContains(co, file),
      MatchesNode m => EvalMatches(m, file),
      _ => throw new InvalidOperationException($"Unexpected node {node.GetType().Name}."),
    };

    private static bool EvalCompare(CompareNode node, IFilterFileContext file) {
      var lhs = GetField(node.Field, file);
      var rhs = node.Rhs.Evaluate(file, node.Field);

      if (lhs is null || rhs is null)
        // A missing-field comparison is always false, except for != null vs null which is true.
        return node.Op == CompareOp.Ne && !(lhs is null && rhs is null);

      var cmp = CompareValues(lhs, rhs);
      return node.Op switch {
        CompareOp.Eq => cmp == 0,
        CompareOp.Ne => cmp != 0,
        CompareOp.Lt => cmp < 0,
        CompareOp.Le => cmp <= 0,
        CompareOp.Gt => cmp > 0,
        CompareOp.Ge => cmp >= 0,
        _ => false,
      };
    }

    private static bool EvalIn(InNode node, IFilterFileContext file) {
      var lhs = GetField(node.Field, file);
      if (lhs is null) return false;
      foreach (var v in node.Values) {
        var rv = v.Evaluate(file, node.Field);
        if (rv is null) continue;
        if (CompareValues(lhs, rv) == 0) return true;
      }
      return false;
    }

    private static bool EvalContains(ContainsNode node, IFilterFileContext file) {
      var lhs = GetField(node.Field, file);
      return lhs is string s && s.Contains(node.Needle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvalMatches(MatchesNode node, IFilterFileContext file) {
      var lhs = GetField(node.Field, file);
      return lhs is string s && node.Pattern.IsMatch(s);
    }

    private static object? GetField(DefragSortField f, IFilterFileContext file) => f switch {
      DefragSortField.Name => file.Name,
      DefragSortField.Path => file.Path,
      DefragSortField.Extension => file.Extension,
      DefragSortField.Size => (double)file.Size,
      DefragSortField.LastModified => file.LastModified.HasValue ? (object?)file.LastModified.Value : null,
      DefragSortField.LastAccessed => file.LastAccessed.HasValue ? (object?)file.LastAccessed.Value : null,
      DefragSortField.Created => file.Created.HasValue ? (object?)file.Created.Value : null,
      DefragSortField.Attributes => (double)file.Attributes,
      _ => null,
    };

    private static int CompareValues(object lhs, object rhs) {
      if (lhs is DateTime dl && rhs is DateTime dr) return dl.CompareTo(dr);
      if (lhs is double || rhs is double) {
        var l = Convert.ToDouble(lhs, CultureInfo.InvariantCulture);
        var r = Convert.ToDouble(rhs, CultureInfo.InvariantCulture);
        return l.CompareTo(r);
      }
      if (lhs is string ls && rhs is string rs)
        return string.Compare(ls, rs, StringComparison.OrdinalIgnoreCase);
      if (lhs is bool bl && rhs is bool br) return bl.CompareTo(br);
      throw new InvalidOperationException(
        $"Cannot compare {lhs.GetType().Name} with {rhs.GetType().Name}.");
    }
  }

  // ── Lexer ──────────────────────────────────────────────────────────────

  private enum TokKind {
    Ident, Number, String, LParen, RParen, Comma,
    Eq, Ne, Lt, Le, Gt, Ge, Plus, Minus,
    And, Or, Not, In, Contains, Matches,
    True, False, Null,
    Eof,
  }

  private readonly record struct Token(TokKind Kind, string Text, int Position);

  // ── Parser ─────────────────────────────────────────────────────────────

  private sealed class Parser {

    private readonly List<Token> _tokens;
    private int _pos;

    public Parser(string source) {
      this._tokens = Tokenise(source);
      this._pos = 0;
    }

    public Node ParseExpr() => this.ParseOr();

    public void ExpectEof() {
      var t = this.Peek();
      if (t.Kind != TokKind.Eof)
        throw new FormatException($"Unexpected token '{t.Text}' at position {t.Position}.");
    }

    private Node ParseOr() {
      var left = this.ParseAnd();
      while (this.Peek().Kind == TokKind.Or) {
        this.Consume();
        var right = this.ParseAnd();
        left = new OrNode(left, right);
      }
      return left;
    }

    private Node ParseAnd() {
      var left = this.ParsePrimary();
      while (this.Peek().Kind == TokKind.And) {
        this.Consume();
        var right = this.ParsePrimary();
        left = new AndNode(left, right);
      }
      return left;
    }

    private Node ParsePrimary() {
      var t = this.Peek();
      if (t.Kind == TokKind.LParen) {
        this.Consume();
        var inner = this.ParseOr();
        this.Expect(TokKind.RParen);
        return inner;
      }
      if (t.Kind == TokKind.Not) {
        this.Consume();
        return new NotNode(this.ParsePrimary());
      }
      return this.ParseComparison();
    }

    private Node ParseComparison() {
      var fieldTok = this.Peek();
      if (fieldTok.Kind != TokKind.Ident)
        throw new FormatException($"Expected field name at position {fieldTok.Position}, got '{fieldTok.Text}'.");
      DefragSortField field;
      try {
        field = ResolveField(fieldTok.Text);
      } catch (FormatException) {
        throw new FormatException($"unknown field '{fieldTok.Text}' at position {fieldTok.Position}");
      }
      this.Consume();

      var opTok = this.Peek();
      switch (opTok.Kind) {
        case TokKind.Contains: {
          this.Consume();
          var v = this.ParseValue(field);
          if (v is not StringValue sv)
            throw new FormatException($"'contains' RHS must be a string at position {opTok.Position}.");
          return new ContainsNode(field, sv.Text);
        }
        case TokKind.Matches: {
          this.Consume();
          var v = this.ParseValue(field);
          if (v is not StringValue sv)
            throw new FormatException($"'matches' RHS must be a string at position {opTok.Position}.");
          Regex regex;
          try {
            regex = new Regex(sv.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
          } catch (ArgumentException ex) {
            throw new FormatException($"Invalid regex '{sv.Text}' at position {opTok.Position}: {ex.Message}", ex);
          }
          return new MatchesNode(field, regex);
        }
        case TokKind.In: {
          this.Consume();
          this.Expect(TokKind.LParen);
          var values = new List<ValueNode>();
          if (this.Peek().Kind != TokKind.RParen) {
            values.Add(this.ParseValue(field));
            while (this.Peek().Kind == TokKind.Comma) {
              this.Consume();
              values.Add(this.ParseValue(field));
            }
          }
          this.Expect(TokKind.RParen);
          return new InNode(field, values);
        }
        case TokKind.Eq: case TokKind.Ne: case TokKind.Lt: case TokKind.Le: case TokKind.Gt: case TokKind.Ge: {
          this.Consume();
          var v = this.ParseValue(field);
          var op = opTok.Kind switch {
            TokKind.Eq => CompareOp.Eq,
            TokKind.Ne => CompareOp.Ne,
            TokKind.Lt => CompareOp.Lt,
            TokKind.Le => CompareOp.Le,
            TokKind.Gt => CompareOp.Gt,
            _ => CompareOp.Ge,
          };
          return new CompareNode(field, op, v);
        }
        default:
          throw new FormatException($"Expected operator after field '{fieldTok.Text}' at position {opTok.Position}, got '{opTok.Text}'.");
      }
    }

    // value := term ( (+|-) term )*
    private ValueNode ParseValue(DefragSortField field) {
      var left = this.ParseTerm(field);
      while (this.Peek().Kind is TokKind.Plus or TokKind.Minus) {
        var opTok = this.Consume();
        var right = this.ParseTerm(field);
        left = new BinaryValue(left, opTok.Kind == TokKind.Plus ? '+' : '-', right);
      }
      return left;
    }

    private ValueNode ParseTerm(DefragSortField field) {
      var t = this.Peek();
      switch (t.Kind) {
        case TokKind.Number:
          this.Consume();
          return ParseNumericOrSize(t.Text, t.Position);
        case TokKind.String:
          this.Consume();
          if (IsDateField(field) && TryParseDate(t.Text, out var dt))
            return new DateValue(dt);
          return new StringValue(t.Text);
        case TokKind.True:
          this.Consume();
          return new BoolValue(true);
        case TokKind.False:
          this.Consume();
          return new BoolValue(false);
        case TokKind.Null:
          this.Consume();
          return new NullValue();
        case TokKind.LParen: {
          this.Consume();
          var inner = this.ParseValue(field);
          this.Expect(TokKind.RParen);
          return inner;
        }
        case TokKind.Ident: {
          this.Consume();
          if (this.Peek().Kind != TokKind.LParen)
            throw new FormatException($"Unexpected identifier '{t.Text}' at position {t.Position}.");
          this.Consume();
          var args = new List<ValueNode>();
          if (this.Peek().Kind != TokKind.RParen) {
            args.Add(this.ParseValue(field));
            while (this.Peek().Kind == TokKind.Comma) {
              this.Consume();
              args.Add(this.ParseValue(field));
            }
          }
          this.Expect(TokKind.RParen);
          return ApplyFunction(t.Text, args, t.Position);
        }
        default:
          throw new FormatException($"Expected value at position {t.Position}, got '{t.Text}'.");
      }
    }

    private static bool IsDateField(DefragSortField f)
      => f is DefragSortField.LastModified or DefragSortField.LastAccessed or DefragSortField.Created;

    private static ValueNode ApplyFunction(string name, List<ValueNode> args, int pos) {
      switch (name.ToLowerInvariant()) {
        case "quartile": {
          if (args.Count != 1 || args[0] is not NumberValue nv)
            throw new FormatException($"quartile() takes one numeric argument at position {pos}.");
          if (nv.Value < 0 || nv.Value > 1)
            throw new FormatException($"quartile() argument must be in [0..1] at position {pos}, got {nv.Value}.");
          return new QuartileValue(nv.Value);
        }
        case "now": {
          if (args.Count != 0)
            throw new FormatException($"now() takes no arguments at position {pos}.");
          return new DateValue(DateTime.UtcNow);
        }
        case "today": {
          if (args.Count != 0)
            throw new FormatException($"today() takes no arguments at position {pos}.");
          return new DateValue(DateTime.UtcNow.Date);
        }
        case "date": {
          if (args.Count != 1 || args[0] is not StringValue sv)
            throw new FormatException($"date() takes one string argument at position {pos}.");
          if (!TryParseDate(sv.Text, out var dt))
            throw new FormatException($"Invalid date '{sv.Text}' at position {pos}.");
          return new DateValue(dt);
        }
        case "days": {
          if (args.Count != 1 || args[0] is not NumberValue n)
            throw new FormatException($"days() takes one numeric argument at position {pos}.");
          return new DurationValue(TimeSpan.FromDays(n.Value));
        }
        case "hours": {
          if (args.Count != 1 || args[0] is not NumberValue n)
            throw new FormatException($"hours() takes one numeric argument at position {pos}.");
          return new DurationValue(TimeSpan.FromHours(n.Value));
        }
        case "minutes": {
          if (args.Count != 1 || args[0] is not NumberValue n)
            throw new FormatException($"minutes() takes one numeric argument at position {pos}.");
          return new DurationValue(TimeSpan.FromMinutes(n.Value));
        }
        default:
          throw new FormatException($"unknown function '{name}' at position {pos}");
      }
    }

    private static ValueNode ParseNumericOrSize(string text, int pos) {
      var splitAt = text.Length;
      for (var i = 0; i < text.Length; i++) {
        var c = text[i];
        if (!(char.IsDigit(c) || c == '.' || c == 'e' || c == 'E')) {
          splitAt = i;
          break;
        }
      }
      var numText = text[..splitAt];
      var unitText = text[splitAt..].ToUpperInvariant();

      if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        throw new FormatException($"Invalid number '{text}' at position {pos}.");

      if (unitText.Length == 0) return new NumberValue(num);

      long mult = unitText switch {
        "B" => 1L,
        "K" or "KB" or "KIB" => 1024L,
        "M" or "MB" or "MIB" => 1024L * 1024L,
        "G" or "GB" or "GIB" => 1024L * 1024L * 1024L,
        "T" or "TB" or "TIB" => 1024L * 1024L * 1024L * 1024L,
        _ => throw new FormatException($"Unknown size unit '{unitText}' at position {pos}."),
      };
      return new SizeValue((long)(num * mult));
    }

    private static bool TryParseDate(string s, out DateTime result)
      => DateTime.TryParse(s, CultureInfo.InvariantCulture,
          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);

    private Token Peek() => this._tokens[this._pos];
    private Token Consume() => this._tokens[this._pos++];
    private void Expect(TokKind kind) {
      var t = this.Peek();
      if (t.Kind != kind)
        throw new FormatException($"Expected {kind} at position {t.Position}, got '{t.Text}'.");
      this.Consume();
    }

    private static List<Token> Tokenise(string src) {
      var list = new List<Token>();
      var i = 0;
      while (i < src.Length) {
        var c = src[i];
        if (char.IsWhiteSpace(c)) { i++; continue; }
        switch (c) {
          case '(': list.Add(new Token(TokKind.LParen, "(", i)); i++; continue;
          case ')': list.Add(new Token(TokKind.RParen, ")", i)); i++; continue;
          case ',': list.Add(new Token(TokKind.Comma, ",", i)); i++; continue;
          case '+': list.Add(new Token(TokKind.Plus, "+", i)); i++; continue;
          case '-': list.Add(new Token(TokKind.Minus, "-", i)); i++; continue;
          case '=':
            if (i + 1 < src.Length && src[i + 1] == '=') { list.Add(new Token(TokKind.Eq, "==", i)); i += 2; continue; }
            list.Add(new Token(TokKind.Eq, "=", i)); i++; continue;
          case '!':
            if (i + 1 < src.Length && src[i + 1] == '=') { list.Add(new Token(TokKind.Ne, "!=", i)); i += 2; continue; }
            throw new FormatException($"Unexpected '!' at position {i}.");
          case '<':
            if (i + 1 < src.Length && src[i + 1] == '=') { list.Add(new Token(TokKind.Le, "<=", i)); i += 2; continue; }
            if (i + 1 < src.Length && src[i + 1] == '>') { list.Add(new Token(TokKind.Ne, "<>", i)); i += 2; continue; }
            list.Add(new Token(TokKind.Lt, "<", i)); i++; continue;
          case '>':
            if (i + 1 < src.Length && src[i + 1] == '=') { list.Add(new Token(TokKind.Ge, ">=", i)); i += 2; continue; }
            list.Add(new Token(TokKind.Gt, ">", i)); i++; continue;
          case '"': case '\'': {
            var quote = c; var start = i + 1; var sb = new StringBuilder();
            i++;
            while (i < src.Length && src[i] != quote) {
              if (src[i] == '\\' && i + 1 < src.Length) {
                sb.Append(src[i + 1]); i += 2;
              } else {
                sb.Append(src[i]); i++;
              }
            }
            if (i >= src.Length)
              throw new FormatException($"Unterminated string starting at position {start - 1}.");
            i++; // consume closing quote
            list.Add(new Token(TokKind.String, sb.ToString(), start - 1));
            continue;
          }
        }

        if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1]))) {
          var start = i;
          while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) i++;
          // Suffix unit letters (e.g. "MB", "KiB").
          while (i < src.Length && char.IsLetter(src[i])) i++;
          list.Add(new Token(TokKind.Number, src[start..i], start));
          continue;
        }

        if (char.IsLetter(c) || c == '_') {
          var start = i;
          while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
          var ident = src[start..i];
          var kind = ident.ToLowerInvariant() switch {
            "and" => TokKind.And,
            "or" => TokKind.Or,
            "not" => TokKind.Not,
            "in" => TokKind.In,
            "contains" => TokKind.Contains,
            "matches" => TokKind.Matches,
            "true" => TokKind.True,
            "false" => TokKind.False,
            "null" => TokKind.Null,
            _ => TokKind.Ident,
          };
          list.Add(new Token(kind, ident, start));
          continue;
        }

        throw new FormatException($"Unexpected character '{c}' at position {i}.");
      }
      list.Add(new Token(TokKind.Eof, "<eof>", src.Length));
      return list;
    }
  }

  // ── Field resolver ─────────────────────────────────────────────────────

  internal static DefragSortField ResolveField(string s) {
    Span<char> buf = stackalloc char[s.Length];
    var idx = 0;
    foreach (var c in s) {
      if (c == '_' || c == '-' || c == ' ') continue;
      buf[idx++] = char.ToLowerInvariant(c);
    }
    var normalised = new string(buf[..idx]);
    return normalised switch {
      "name" => DefragSortField.Name,
      "path" => DefragSortField.Path,
      "extension" or "ext" => DefragSortField.Extension,
      "size" or "length" => DefragSortField.Size,
      "lastmodified" or "mtime" or "modified" => DefragSortField.LastModified,
      "lastaccessed" or "atime" or "accessed" => DefragSortField.LastAccessed,
      "created" or "ctime" or "creationtime" => DefragSortField.Created,
      "attributes" or "attrs" or "attr" => DefragSortField.Attributes,
      _ => throw new FormatException($"Unknown field '{s}'."),
    };
  }
}
