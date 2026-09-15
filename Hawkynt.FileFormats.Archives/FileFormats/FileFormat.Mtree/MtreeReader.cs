using System.Globalization;
using System.Text;

namespace FileFormat.Mtree;

/// <summary>
/// Reads BSD mtree(5) manifests, including <c>/set</c>/<c>/unset</c>, full-path
/// entries, classic relative-directory traversal, and octal pathname escapes.
/// </summary>
public sealed class MtreeReader {
  private readonly Stream _stream;

  /// <summary>Initializes a reader over <paramref name="stream"/>.</summary>
  public MtreeReader(Stream stream) => this._stream = stream ?? throw new ArgumentNullException(nameof(stream));

  /// <summary>Reads all manifest entries.</summary>
  public List<MtreeEntry> ReadAll() {
    if (this._stream.CanSeek)
      this._stream.Position = 0;

    using var reader = new StreamReader(
      this._stream,
      Encoding.UTF8,
      detectEncodingFromByteOrderMarks: true,
      bufferSize: 4096,
      leaveOpen: true);

    var result = new List<MtreeEntry>();
    var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    var currentDirectory = "";
    string? line;
    while ((line = reader.ReadLine()) != null) {
      var trimmed = line.TrimStart();
      if (trimmed.Length == 0 || trimmed[0] == '#')
        continue;

      var tokens = SplitWhitespace(trimmed);
      if (tokens.Count == 0)
        continue;

      if (tokens[0] == "/set") {
        ApplyKeywordTokens(defaults, tokens, 1);
        continue;
      }

      if (tokens[0] == "/unset") {
        for (var i = 1; i < tokens.Count; ++i) {
          var key = tokens[i];
          if (key.Equals("all", StringComparison.OrdinalIgnoreCase))
            defaults.Clear();
          else
            defaults.Remove(key);
        }
        continue;
      }

      var decodedName = DecodeToken(tokens[0]);
      if (decodedName == "..") {
        currentDirectory = ParentPath(currentDirectory);
        continue;
      }

      var keywords = new Dictionary<string, string?>(defaults, StringComparer.OrdinalIgnoreCase);
      ApplyKeywordTokens(keywords, tokens, 1);

      var isFullEntry = decodedName.Length > 1 && decodedName.AsSpan(1).Contains('/');
      var path = isFullEntry
        ? NormalizeFullPath(decodedName)
        : JoinPath(currentDirectory, decodedName);

      var type = ParseType(GetValue(keywords, "type"));
      if (!isFullEntry && type == MtreeEntryType.Directory)
        currentDirectory = path == "." ? "" : path;

      // The conventional root entry is metadata for the manifest root, not a
      // member callers expect to extract/list alongside its children.
      if (path is "" or ".")
        continue;

      var entry = new MtreeEntry {
        Path = path,
        Type = type,
        Size = TryParseInt64(GetValue(keywords, "size")),
        Mode = TryParseOctalUInt32(GetValue(keywords, "mode")),
        Uid = TryParseUInt32(GetValue(keywords, "uid")),
        Gid = TryParseUInt32(GetValue(keywords, "gid")),
        ModificationTime = TryParseTime(GetValue(keywords, "time")),
        LinkTarget = DecodeNullable(GetValue(keywords, "link")),
        ContentsPath = DecodeNullable(GetValue(keywords, "contents")),
        Keywords = keywords,
      };
      result.Add(entry);
    }

    return result;
  }

  private static void ApplyKeywordTokens(
    Dictionary<string, string?> destination,
    IReadOnlyList<string> tokens,
    int startIndex
  ) {
    for (var i = startIndex; i < tokens.Count; ++i) {
      var token = tokens[i];
      var equals = token.IndexOf('=');
      if (equals < 0) {
        destination[token] = null;
        continue;
      }

      var key = token[..equals];
      var value = token[(equals + 1)..];
      destination[key] = value;
    }
  }

  private static List<string> SplitWhitespace(string line) {
    var result = new List<string>();
    var start = -1;
    for (var i = 0; i <= line.Length; ++i) {
      var isSeparator = i == line.Length || char.IsWhiteSpace(line[i]);
      if (!isSeparator) {
        if (start < 0)
          start = i;
        continue;
      }

      if (start >= 0) {
        result.Add(line[start..i]);
        start = -1;
      }
    }
    return result;
  }

  private static string JoinPath(string directory, string name) {
    if (name == ".")
      return directory.Length == 0 ? "." : directory;
    return directory.Length == 0 ? name : $"{directory}/{name}";
  }

  private static string NormalizeFullPath(string path) {
    while (path.StartsWith("./", StringComparison.Ordinal))
      path = path[2..];
    return path;
  }

  private static string ParentPath(string path) {
    var separator = path.LastIndexOf('/');
    return separator < 0 ? "" : path[..separator];
  }

  private static string? GetValue(IReadOnlyDictionary<string, string?> keywords, string key)
    => keywords.TryGetValue(key, out var value) ? value : null;

  private static string? DecodeNullable(string? value)
    => value == null ? null : DecodeToken(value);

  internal static string DecodeToken(string token) {
    var source = Encoding.UTF8.GetBytes(token);
    var decoded = new byte[source.Length];
    var write = 0;
    for (var read = 0; read < source.Length;) {
      if (source[read] == (byte)'\\' && read + 3 < source.Length &&
          IsOctal(source[read + 1]) && IsOctal(source[read + 2]) && IsOctal(source[read + 3])) {
        decoded[write++] = (byte)(
          ((source[read + 1] - (byte)'0') << 6) |
          ((source[read + 2] - (byte)'0') << 3) |
          (source[read + 3] - (byte)'0'));
        read += 4;
        continue;
      }

      decoded[write++] = source[read++];
    }
    return Encoding.UTF8.GetString(decoded, 0, write);
  }

  private static bool IsOctal(byte value) => value is >= (byte)'0' and <= (byte)'7';

  private static MtreeEntryType ParseType(string? value) => value?.ToLowerInvariant() switch {
    "file" => MtreeEntryType.File,
    "dir" => MtreeEntryType.Directory,
    "link" => MtreeEntryType.Link,
    "block" => MtreeEntryType.Block,
    "char" => MtreeEntryType.Character,
    "fifo" => MtreeEntryType.Fifo,
    "socket" => MtreeEntryType.Socket,
    _ => MtreeEntryType.Unknown,
  };

  private static long? TryParseInt64(string? value)
    => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
      ? parsed
      : null;

  private static uint? TryParseUInt32(string? value)
    => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
      ? parsed
      : null;

  private static uint? TryParseOctalUInt32(string? value) {
    if (string.IsNullOrEmpty(value))
      return null;

    uint result = 0;
    foreach (var c in value) {
      if (c is < '0' or > '7')
        return null;
      try {
        result = checked((result << 3) | (uint)(c - '0'));
      } catch (OverflowException) {
        return null;
      }
    }
    return result;
  }

  private static DateTimeOffset? TryParseTime(string? value) {
    if (string.IsNullOrWhiteSpace(value))
      return null;

    var dot = value.IndexOf('.');
    var secondsText = dot < 0 ? value : value[..dot];
    if (!long.TryParse(secondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
      return null;

    try {
      var result = DateTimeOffset.FromUnixTimeSeconds(seconds);
      if (dot < 0 || dot == value.Length - 1)
        return result;

      var fraction = value[(dot + 1)..];
      var ticksText = fraction.Length >= 7 ? fraction[..7] : fraction.PadRight(7, '0');
      if (long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
        result = result.AddTicks(ticks);
      return result;
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }
}
