using System.Globalization;
using System.Text;

namespace FileFormat.Paq8l;

/// <summary>One file listed in a paq8l header.</summary>
/// <param name="Name">Name with '/' separators, as stored.</param>
/// <param name="Size">Size in bytes.</param>
public sealed record Paq8lEntry(string Name, long Size);

/// <summary>
/// paq8l archives (docs/PAQ8L-FORMAT.md): a text header naming the files and their sizes, then
/// one context-mixing coded stream holding all of them.
/// </summary>
public static class Paq8lArchive {
  /// <summary>The header's first bytes: "paq8l -".</summary>
  public static ReadOnlySpan<byte> Signature => "paq8l -"u8;

  /// <summary>Level paq8l uses by default.</summary>
  public const int DefaultLevel = 5;

  /// <summary>
  /// Reads the header: the level and the files. Returns the offset of the coded data.
  /// </summary>
  public static (int Level, List<Paq8lEntry> Entries, long DataOffset) ReadHeader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var first = ReadLine(stream, out var ended) ?? throw new InvalidDataException("paq8l: empty archive.");
    if (!first.StartsWith("paq8l -", StringComparison.Ordinal) || ended)
      throw new InvalidDataException("paq8l: the header does not start with \"paq8l -\".");
    var level = first.Length > 7 ? first[7] - '0' : -1;
    if (level is < 0 or > 9) level = DefaultLevel;
    var entries = new List<Paq8lEntry>();
    while (ReadLine(stream, out ended) is { } line && !ended) {
      var tab = line.IndexOf('\t');
      if (tab < 0) throw new InvalidDataException("paq8l: a header line has no TAB between size and name.");
      if (!long.TryParse(line.AsSpan(0, tab).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        throw new InvalidDataException($"paq8l: '{line[..tab]}' is not a file size.");
      entries.Add(new(line[(tab + 1)..], size));
    }
    return (level, entries, stream.Position);
  }

  /// <summary>
  /// One header line without CR/LF, or null at the end. <paramref name="ended"/> is true when the
  /// line was cut by SUB (0x1A) or the end of the stream rather than a line feed.
  /// </summary>
  private static string? ReadLine(Stream stream, out bool ended) {
    var bytes = new List<byte>();
    int c;
    while ((c = stream.ReadByte()) >= 0 && c != 26 && c != '\n')
      if (c != '\r') bytes.Add((byte)c);
    ended = c is < 0 or 26;
    if (ended && bytes.Count == 0) return null;
    return Encoding.Latin1.GetString(bytes.ToArray());
  }

  /// <summary>Lists the files without decoding anything.</summary>
  public static List<Paq8lEntry> List(Stream stream) => ReadHeader(stream).Entries;

  /// <summary>Decodes every file, in header order.</summary>
  public static List<(Paq8lEntry Entry, byte[] Data)> Read(Stream stream) {
    var (level, entries, _) = ReadHeader(stream);
    var decoder = new Paq8lDecoder(stream, level);
    var blocks = new Paq8lBlockDecoder();
    var result = new List<(Paq8lEntry, byte[])>(entries.Count);
    var start = stream.Position;
    foreach (var entry in entries) {
      if (entry.Size > Array.MaxLength) throw new InvalidDataException($"paq8l: '{entry.Name}' is too large to decode in memory.");
      var data = new byte[entry.Size];
      for (var i = 0; i < data.Length; ++i) {
        var b = blocks.Next(decoder.Read);
        if (b < 0) throw new InvalidDataException($"paq8l: the coded data of '{entry.Name}' ends early.");
        data[i] = (byte)b;
      }
      result.Add((entry, data));
    }
    if (decoder.PastEnd > 3) throw new InvalidDataException("paq8l: the coded data ends early.");
    return result;
  }

  /// <summary>Writes <paramref name="files"/> at <paramref name="level"/> (0–9).</summary>
  public static void Write(Stream output, IReadOnlyList<(string Name, byte[] Data)> files, int level = DefaultLevel) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    ArgumentOutOfRangeException.ThrowIfLessThan(level, 0);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);
    if (files.Count == 0) throw new ArgumentException("paq8l: an archive holds at least one file.", nameof(files));
    var header = new StringBuilder("paq8l -").Append((char)('0' + level)).Append("\r\n");
    foreach (var (name, data) in files) {
      if (string.IsNullOrEmpty(name) || name.IndexOfAny(['\r', '\n', '\t', '\x1A', '\0']) >= 0)
        throw new ArgumentException($"paq8l: '{name}' cannot be stored as a file name.", nameof(files));
      header.Append(data.Length.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(name).Append("\r\n");
    }
    header.Append('\x1A');
    output.Write(Encoding.Latin1.GetBytes(header.ToString()));

    var encoder = new Paq8lEncoder(output, level);
    var test = new Paq8lBlockDecoder();
    var chunkSize = (0x10000 << level) * 64;
    foreach (var (_, data) in files)
      for (var start = 0; start < data.Length; start += chunkSize) {
        var size = Math.Min(chunkSize, data.Length - start);
        var coded = Paq8lSegmenter.Encode(data, start, size);
        if (Passes(test, coded, data.AsSpan(start, size)))
          foreach (var b in coded) encoder.Write(b);
        else {
          encoder.Write(0);
          encoder.Write(size >> 24);
          encoder.Write(size >> 16);
          encoder.Write(size >> 8);
          encoder.Write(size);
          foreach (var b in data.AsSpan(start, size)) encoder.Write(b);
        }
      }
    encoder.Flush();
  }

  /// <summary>
  /// The format's transform test (§2.3): the coded chunk must decode to the original and be used
  /// up exactly. <paramref name="test"/> keeps its state from chunk to chunk, as the format's does.
  /// </summary>
  private static bool Passes(Paq8lBlockDecoder test, byte[] coded, ReadOnlySpan<byte> original) {
    var at = 0;
    int Source() => at < coded.Length ? coded[at++] : -1;
    for (var j = 0; j < original.Length; ++j)
      if (test.Next(Source) != original[j]) return false;
    return at == coded.Length;
  }
}
