using System.Text;
using Compression.Core.Streams;

namespace FileFormat.Shar;

/// <summary>
/// Reads a shell archive (shar): a <c>/bin/sh</c> script whose here-documents carry the files.
/// </summary>
/// <remarks>
/// <para>The script is not run. Its here-documents are interpreted the way <c>sh</c> feeds them to
/// the command that owns them (POSIX Shell Command Language §2.7.4): every body line is passed on
/// with its newline, byte for byte, carriage returns included, up to the line equal to the
/// delimiter. <c>&lt;&lt;-</c> strips leading tabs first; an unquoted delimiter has the body's
/// backslash escapes and line continuations processed (other expansions are left as written).</para>
/// <para>The owning command decides what becomes a file: <c>cat</c> copies the body, a
/// <c>sed 's/^PREFIX//'</c> strips a literal prefix, and <c>uudecode</c>, directly or at the end of
/// a pipe, decodes a uuencoded or <c>begin-base64</c> body into the file its <c>-o</c> option or
/// <c>begin</c> line names. Text bodies go to the redirection of that command or pipeline; bodies
/// that go nowhere (the MD5 checks, messages) and <c>/dev/null</c> are skipped. A command that is
/// only <c>&gt; file</c> makes an empty file. A file GNU shar compresses (<c>-z</c>, <c>-C</c>) is
/// uudecoded to a temporary file and then unpacked by <c>gzip</c>, <c>bzip2</c> or <c>xz -dc</c> into
/// its real name; those three are decoded here, any other unpacker is refused.</para>
/// <para>This is the layout GNU sharutils 4.15.2 writes, and the reader is checked against what
/// <c>sh</c> extracts from its archives in every mode.</para>
/// </remarks>
public sealed class SharReader {
  private readonly List<SharEntry> _entries = [];

  /// <summary>The entries found in the shar archive.</summary>
  public IReadOnlyList<SharEntry> Entries => this._entries;

  /// <summary>
  /// Reads and parses a shar archive from a stream.
  /// </summary>
  /// <exception cref="NotSupportedException">A file is unpacked by a decompressor other than gzip, bzip2 or xz.</exception>
  public SharReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    this.Parse(SplitLines(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
  }

  /// <summary>Splits at LF only, so a CR stays in its line; bytes map one to one onto chars (Latin-1).</summary>
  private static List<string> SplitLines(ReadOnlySpan<byte> bytes) {
    var lines = new List<string>();
    while (!bytes.IsEmpty) {
      var nl = bytes.IndexOf((byte)'\n');
      lines.Add(Encoding.Latin1.GetString(nl < 0 ? bytes : bytes[..nl]));
      bytes = nl < 0 ? [] : bytes[(nl + 1)..];
    }
    return lines;
  }

  private void Parse(List<string> lines) {
    var i = 0;
    while (i < lines.Count) {
      var line = lines[i++];
      // A backslash ending a command line continues it on the next (GNU shar's "&& \").
      while (EndsWithContinuation(line) && i < lines.Count)
        line = line[..^1] + lines[i++];
      var tokens = ShellTokenizer.Tokenize(line);
      if (tokens == null)
        continue;

      var heredoc = tokens.FindIndex(t => t.Kind is ShellTokenKind.HereDoc or ShellTokenKind.HereDocStripTabs);
      if (heredoc < 0) {
        if (TryParseBegin(line, out var base64, out var bareName)) {
          // A uuencoded block in the script body itself (shars piped to uudecode whole).
          var body = new List<string>();
          while (i < lines.Count && lines[i] != "end" && lines[i] != "====")
            body.Add(lines[i++]);
          if (i < lines.Count) ++i;
          this.Add(bareName, Uudecode(body, base64), append: false);
        } else {
          this.InterpretPlainLine(tokens);
        }
        continue;
      }

      // Read every here-document body the line opens, in order; the first one feeds the file.
      List<string>? first = null;
      for (var k = heredoc; k < tokens.Count; ++k) {
        if (tokens[k].Kind is not (ShellTokenKind.HereDoc or ShellTokenKind.HereDocStripTabs))
          continue;
        var delimiter = k + 1 < tokens.Count && tokens[k + 1].Kind == ShellTokenKind.Word ? tokens[k + 1] : null;
        var body = new List<string>();
        while (delimiter != null && i < lines.Count) {
          var bodyLine = lines[i++];
          if (tokens[k].Kind == ShellTokenKind.HereDocStripTabs)
            bodyLine = bodyLine.TrimStart('\t');
          if (bodyLine == delimiter.Text)
            break;
          body.Add(bodyLine);
        }
        first ??= delimiter is { Quoted: false } ? Unescape(body) : body;
      }

      this.InterpretHereDoc(tokens, heredoc, first!);
    }
  }

  private static bool EndsWithContinuation(string line) {
    var backslashes = 0;
    for (var k = line.Length - 1; k >= 0 && line[k] == '\\'; --k)
      ++backslashes;
    return backslashes % 2 == 1;
  }

  /// <summary>Splits a line at list operators and subshell parentheses into pipelines of simple commands.</summary>
  private static List<List<ShellCommand>> Pipelines(List<ShellToken> tokens, int from, int to) {
    var pipelines = new List<List<ShellCommand>>();
    var pipeline = new List<ShellCommand>();
    var command = new List<ShellToken>();
    for (var k = from; k <= to; ++k) {
      var end = k == to || tokens[k].Kind is ShellTokenKind.ListOperator or ShellTokenKind.Paren;
      if (k < to && tokens[k].Kind == ShellTokenKind.Pipe) {
        pipeline.Add(ShellCommand.From(command));
        command = [];
      } else if (end) {
        pipeline.Add(ShellCommand.From(command));
        pipelines.Add(pipeline);
        pipeline = [];
        command = [];
      } else {
        command.Add(tokens[k]);
      }
    }
    return pipelines;
  }

  /// <summary>Lines without a here-document: empty files and the unpacking of compressed ones.</summary>
  private void InterpretPlainLine(List<ShellToken> tokens) {
    foreach (var pipeline in Pipelines(tokens, 0, tokens.Count)) {
      var command = pipeline[0];
      if (pipeline.Count == 1 && command.Name == null && command.StdoutTarget != null && command.StdinSource == null) {
        this.Add(command.StdoutTarget, [], command.Appends);
        continue;
      }
      if (command.Name == null || !Unpackers.Contains(Path.GetFileName(command.Name)) || pipeline[^1].StdoutTarget is not { } target)
        continue;

      var source = command.StdinSource ?? command.Arguments.LastOrDefault(a => !a.StartsWith('-'));
      var pending = source == null ? -1 : this.FindTemporary(source);
      if (pending < 0)
        continue;

      var packed = this._entries[pending].Data;
      this._entries.RemoveAt(pending);
      this.Add(target, Unpack(command.Name, packed), pipeline[^1].Appends);
    }
  }

  /// <summary>
  /// The entry a later command reads back: GNU shar uudecodes a compressed file to
  /// <c>${lock_dir}/gzi</c> (or <c>bzi</c>, <c>xzi</c>) and unpacks it from there. The variable is not
  /// expanded, so the file is matched by its last path segment.
  /// </summary>
  private int FindTemporary(string source) {
    var name = source[(source.LastIndexOf('/') + 1)..];
    if (name.Length == 0)
      return -1;
    for (var k = this._entries.Count - 1; k >= 0; --k)
      if (this._entries[k].FileName.EndsWith("/" + name, StringComparison.Ordinal) || this._entries[k].FileName == name)
        return k;
    return -1;
  }

  /// <summary>Commands that unpack a compressed file; only gzip, bzip2 and xz are decoded.</summary>
  private static readonly HashSet<string> Unpackers = [
    "gzip", "gunzip", "zcat", "bzip2", "bunzip2", "bzcat", "xz", "unxz", "xzcat",
    "compress", "uncompress", "lzma", "unlzma", "lzip", "zstd", "unzstd", "lz4",
  ];

  private static byte[] Unpack(string command, byte[] packed) {
    using var input = new MemoryStream(packed);
    using Stream decoder = Path.GetFileName(command) switch {
      "gzip" or "gunzip" or "zcat" => new FileFormat.Gzip.GzipStream(input, CompressionStreamMode.Decompress),
      "bzip2" or "bunzip2" or "bzcat" => new FileFormat.Bzip2.Bzip2Stream(input, CompressionStreamMode.Decompress),
      "xz" or "unxz" or "xzcat" => new FileFormat.Xz.XzStream(input, CompressionStreamMode.Decompress),
      var other => throw new NotSupportedException($"shar: files unpacked with '{other}' are not supported."),
    };
    using var output = new MemoryStream();
    decoder.CopyTo(output);
    return output.ToArray();
  }

  private void InterpretHereDoc(List<ShellToken> tokens, int heredoc, List<string> body) {
    var pipeline = FindPipeline(tokens, heredoc);
    if (pipeline == null)
      return;

    var owner = pipeline[0];
    string? prefix = null;
    switch (owner.Name) {
      case "cat" or "uudecode":
        break;
      case "sed":
        prefix = owner.Arguments.Select(SedStripPrefix).FirstOrDefault(p => p != null);
        if (prefix == null)
          return; // another sed program: not a plain copy
        break;
      default:
        return;
    }

    var text = new List<string>(body.Count);
    foreach (var bodyLine in body)
      text.Add(prefix != null && bodyLine.StartsWith(prefix, StringComparison.Ordinal) ? bodyLine[prefix.Length..] : bodyLine);

    var uudecode = pipeline.FirstOrDefault(c => c.Name == "uudecode");
    if (uudecode != null) {
      var begin = text.FindIndex(l => TryParseBegin(l, out _, out _));
      if (begin < 0)
        return;
      TryParseBegin(text[begin], out var base64, out var beginName);
      var target = uudecode.OptionValue("-o") ?? beginName;
      if (target is "/dev/stdout" or "-")
        target = pipeline[^1].StdoutTarget;
      if (target != null)
        this.Add(target, Uudecode(text.Skip(begin + 1).ToList(), base64), append: false);
      return;
    }

    var sink = owner.StdoutTarget != null ? owner : pipeline[^1];
    if (sink.StdoutTarget == null)
      return;
    var content = new StringBuilder();
    foreach (var l in text)
      content.Append(l).Append('\n');
    this.Add(sink.StdoutTarget, Encoding.Latin1.GetBytes(content.ToString()), sink.Appends);
  }

  /// <summary>The pipeline (simple commands joined by pipes) that contains token <paramref name="index"/>.</summary>
  private static List<ShellCommand>? FindPipeline(List<ShellToken> tokens, int index) {
    var start = index;
    while (start > 0 && tokens[start - 1].Kind is not (ShellTokenKind.ListOperator or ShellTokenKind.Paren)) --start;
    var end = index;
    while (end < tokens.Count && tokens[end].Kind is not (ShellTokenKind.ListOperator or ShellTokenKind.Paren)) ++end;
    var pipelines = Pipelines(tokens, start, end);
    return pipelines.Count > 0 ? pipelines[0] : null;
  }

  private void Add(string rawName, byte[] data, bool append) {
    if (rawName is "/dev/null" or "")
      return;
    // Names are raw bytes in the script; ours are UTF-8.
    var name = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(rawName));
    while (name.StartsWith("./", StringComparison.Ordinal))
      name = name[2..];
    var existing = this._entries.FindIndex(e => e.FileName == name);
    if (existing >= 0) {
      // sh writes the same path again: '>' replaces it, '>>' extends it.
      var merged = append ? [.. this._entries[existing].Data, .. data] : data;
      this._entries[existing] = new SharEntry { FileName = name, Data = merged };
      return;
    }
    this._entries.Add(new SharEntry { FileName = name, Data = data });
  }

  /// <summary>The prefix a <c>s/^PREFIX//</c> program removes, when the program is only that and the prefix is literal.</summary>
  private static string? SedStripPrefix(string program) {
    if (program.Length < 6 || !program.StartsWith("s/^", StringComparison.Ordinal) || !program.EndsWith("//", StringComparison.Ordinal))
      return null;
    var prefix = program[3..^2];
    return prefix.IndexOfAny(['\\', '.', '*', '[', ']', '$', '^', '/']) < 0 ? prefix : null;
  }

  /// <summary>Parses <c>begin MODE NAME</c> or <c>begin-base64 MODE NAME</c>.</summary>
  private static bool TryParseBegin(string line, out bool base64, out string name) {
    name = "";
    base64 = line.StartsWith("begin-base64 ", StringComparison.Ordinal);
    if (!base64 && !line.StartsWith("begin ", StringComparison.Ordinal))
      return false;
    var rest = line[(base64 ? 13 : 6)..];
    var space = rest.IndexOf(' ');
    if (space <= 0 || !rest[..space].All(char.IsAsciiDigit))
      return false;
    name = rest[(space + 1)..];
    return name.Length > 0;
  }

  private static byte[] Uudecode(List<string> lines, bool base64) {
    using var ms = new MemoryStream();
    foreach (var line in lines) {
      if (base64) {
        if (line == "====") break;
        ms.Write(Convert.FromBase64String(line.Trim()));
        continue;
      }
      if (line == "end") break;
      if (line.Length == 0) continue;
      var len = (line[0] - 32) & 0x3F;
      if (len == 0) continue;
      var idx = 1;
      for (var i = 0; i < len; i += 3) {
        var c0 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        var c1 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        var c2 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        var c3 = idx < line.Length ? (line[idx++] - 32) & 0x3F : 0;
        ms.WriteByte((byte)((c0 << 2) | (c1 >> 4)));
        if (i + 1 < len) ms.WriteByte((byte)(((c1 & 0xF) << 4) | (c2 >> 2)));
        if (i + 2 < len) ms.WriteByte((byte)(((c2 & 0x3) << 6) | c3));
      }
    }
    return ms.ToArray();
  }

  /// <summary>
  /// The body of an unquoted here-document: <c>\$</c>, <c>\`</c> and <c>\\</c> lose the backslash and a
  /// backslash ending a line joins the next one. Substitutions are left as written.
  /// </summary>
  private static List<string> Unescape(List<string> body) {
    var result = new List<string>();
    var current = new StringBuilder();
    foreach (var line in body) {
      var joined = false;
      for (var k = 0; k < line.Length; ++k) {
        if (line[k] == '\\' && k + 1 < line.Length && line[k + 1] is '$' or '`' or '\\')
          current.Append(line[++k]);
        else if (line[k] == '\\' && k + 1 == line.Length)
          joined = true;
        else
          current.Append(line[k]);
      }
      if (!joined) {
        result.Add(current.ToString());
        current.Clear();
      }
    }
    if (current.Length > 0)
      result.Add(current.ToString());
    return result;
  }
}
