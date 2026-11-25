using System.Globalization;
using System.Text;

namespace FileFormat.Shar;

/// <summary>Kinds of shell tokens the shar reader distinguishes.</summary>
internal enum ShellTokenKind { Word, HereDoc, HereDocStripTabs, Redirect, Pipe, ListOperator, Paren }

/// <summary>One shell token.</summary>
/// <param name="Kind">What the token is.</param>
/// <param name="Text">Word text after quote removal, or the operator.</param>
/// <param name="Quoted">The word had quoting (an unquoted here-document delimiter means an expanded body).</param>
/// <param name="Fd">The file descriptor a redirection applies to.</param>
internal sealed record ShellToken(ShellTokenKind Kind, string Text, bool Quoted = false, int Fd = 1);

/// <summary>
/// Just enough of POSIX token recognition (Shell Command Language §2.3) to read the command lines
/// of a shell archive: quoting, operators, IO numbers and comments. Nothing is expanded.
/// </summary>
internal static class ShellTokenizer {
  private static readonly string[] Operators = ["<<-", "<<", ">>", ">&", "<&", ">|", "&&", "||", ">", "<", "|", ";", "&", "(", ")"];

  /// <summary>Tokenizes one line; null when the line does not close its quotes or continues on the next.</summary>
  public static List<ShellToken>? Tokenize(string line) {
    var tokens = new List<ShellToken>();
    var word = new StringBuilder();
    var inWord = false;
    var quoted = false;

    void EndWord() {
      if (inWord) tokens.Add(new ShellToken(ShellTokenKind.Word, word.ToString(), quoted));
      word.Clear();
      inWord = quoted = false;
    }

    for (var k = 0; k < line.Length; ++k) {
      var c = line[k];
      switch (c) {
        case ' ' or '\t':
          EndWord();
          continue;
        case '#' when !inWord:
          return tokens;
        case '\'': {
          var close = line.IndexOf('\'', k + 1);
          if (close < 0) return null;
          word.Append(line, k + 1, close - k - 1);
          inWord = quoted = true;
          k = close;
          continue;
        }
        case '"': {
          var j = k + 1;
          for (; j < line.Length && line[j] != '"'; ++j) {
            if (line[j] == '\\' && j + 1 < line.Length && line[j + 1] is '"' or '\\' or '$' or '`')
              ++j;
            word.Append(line[j]);
          }
          if (j >= line.Length) return null;
          inWord = quoted = true;
          k = j;
          continue;
        }
        case '\\':
          if (k + 1 >= line.Length) return null;
          word.Append(line[++k]);
          inWord = quoted = true;
          continue;
      }

      var op = Array.Find(Operators, o => string.CompareOrdinal(line, k, o, 0, o.Length) == 0);
      if (op == null) {
        word.Append(c);
        inWord = true;
        continue;
      }

      // Digits written directly before a redirection name the descriptor it applies to.
      var fd = op[0] == '<' ? 0 : 1;
      if (op[0] is '<' or '>' && inWord && !quoted && word.ToString().All(char.IsAsciiDigit)) {
        fd = int.Parse(word.ToString(), CultureInfo.InvariantCulture);
        word.Clear();
        inWord = false;
      } else {
        EndWord();
      }

      tokens.Add(op switch {
        "<<" => new ShellToken(ShellTokenKind.HereDoc, op, Fd: fd),
        "<<-" => new ShellToken(ShellTokenKind.HereDocStripTabs, op, Fd: fd),
        "|" => new ShellToken(ShellTokenKind.Pipe, op),
        "&&" or "||" or ";" or "&" => new ShellToken(ShellTokenKind.ListOperator, op),
        "(" or ")" => new ShellToken(ShellTokenKind.Paren, op),
        _ => new ShellToken(ShellTokenKind.Redirect, op, Fd: fd),
      });
      k += op.Length - 1;
    }

    EndWord();
    return tokens;
  }
}

/// <summary>A simple command: its words, and where its standard input and output are redirected.</summary>
internal sealed class ShellCommand {
  private ShellCommand(List<string> words) {
    this.Name = words.Count > 0 ? words[0] : null;
    this.Arguments = words.Skip(1).ToList();
  }

  /// <summary>The command name, or null for a command made of redirections only.</summary>
  public string? Name { get; }

  /// <summary>The words after the name.</summary>
  public List<string> Arguments { get; }

  /// <summary>The file standard output is redirected to, if any.</summary>
  public string? StdoutTarget { get; private set; }

  /// <summary>Standard output appends (<c>&gt;&gt;</c>).</summary>
  public bool Appends { get; private set; }

  /// <summary>The file standard input is redirected from, if any.</summary>
  public string? StdinSource { get; private set; }

  /// <summary>Builds the command from the tokens between pipes and list operators.</summary>
  public static ShellCommand From(IReadOnlyList<ShellToken> tokens) {
    var words = new List<string>();
    string? stdout = null, stdin = null;
    var appends = false;
    for (var k = 0; k < tokens.Count; ++k) {
      var t = tokens[k];
      if (t.Kind == ShellTokenKind.Word) {
        words.Add(t.Text);
        continue;
      }
      if (t.Kind is not (ShellTokenKind.HereDoc or ShellTokenKind.HereDocStripTabs or ShellTokenKind.Redirect))
        continue;
      var target = k + 1 < tokens.Count && tokens[k + 1].Kind == ShellTokenKind.Word ? tokens[++k].Text : null;
      if (t.Kind != ShellTokenKind.Redirect)
        continue;
      if (t.Fd == 1 && t.Text is ">" or ">>" or ">|") {
        stdout = target;
        appends = t.Text == ">>";
      } else if (t.Fd == 0 && t.Text == "<") {
        stdin = target;
      }
    }
    return new ShellCommand(words) { StdoutTarget = stdout, Appends = appends, StdinSource = stdin };
  }

  /// <summary>The value of an option written <c>-o value</c> or <c>-ovalue</c>.</summary>
  public string? OptionValue(string option) {
    for (var k = 0; k < this.Arguments.Count; ++k) {
      var arg = this.Arguments[k];
      if (arg == option)
        return k + 1 < this.Arguments.Count ? this.Arguments[k + 1] : null;
      if (arg.Length > option.Length && arg.StartsWith(option, StringComparison.Ordinal))
        return arg[option.Length..];
    }
    return null;
  }
}
