using System.Globalization;
using System.Text;

namespace FileFormat.Kgb;

/// <summary>One file of a KGB archive: its name as stored and its size.</summary>
/// <param name="Name">The name from the header line, '/'-separated.</param>
/// <param name="Size">The decoded size in bytes.</param>
public sealed record KgbEntry(string Name, long Size);

/// <summary>
/// Reads and writes KGB Archiver 1.0 archives (docs/KGB-ON-DISK.md): a text header naming every
/// file and its size, then one arithmetic-coded stream holding all files back to back.
/// </summary>
public sealed class KgbArchive {
  /// <summary>The fixed start of the first header line.</summary>
  public const string Signature = "KGB_arch";

  /// <summary>The memory level the archive's default (header without a level digit) implies.</summary>
  public const int DefaultLevel = 3;

  private KgbArchive(int level, IReadOnlyList<KgbEntry> entries, long dataOffset) {
    this.Level = level;
    this.Entries = entries;
    this.DataOffset = dataOffset;
  }

  /// <summary>The memory level 0..9 that sizes the model.</summary>
  public int Level { get; }

  /// <summary>The files, in stream order.</summary>
  public IReadOnlyList<KgbEntry> Entries { get; }

  /// <summary>Where the coded stream starts.</summary>
  public long DataOffset { get; }

  /// <summary>Parses the header of the archive in <paramref name="stream"/>, from its start.</summary>
  /// <exception cref="InvalidDataException">The stream is not a KGB archive.</exception>
  public static KgbArchive Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;
    var first = ReadLine(stream);
    if (!first.StartsWith(Signature, StringComparison.Ordinal))
      throw new InvalidDataException("Not a KGB Archiver archive.");
    var level = DefaultLevel;
    if (first.Length > 2 && first[^2] == '-' && first[^1] is >= '0' and <= '9')
      level = first[^1] - '0';

    var entries = new List<KgbEntry>();
    for (;;) {
      var line = ReadLine(stream);
      if (line.Length <= 1)
        break;
      var size = LeadingNumber(line);
      if (size < 0)
        throw new InvalidDataException($"KGB: negative size in header line '{line}'.");
      var tab = line.IndexOf('\t');
      entries.Add(new KgbEntry(tab >= 0 ? line[(tab + 1)..] : "", size));
    }
    if (stream.ReadByte() != 0x0C || stream.ReadByte() != 0x00)
      throw new InvalidDataException("KGB: the header does not end in 1A 0C 00.");
    return new KgbArchive(level, entries, stream.Position);
  }

  /// <summary>
  /// Decodes the archive's files in order, handing each one to <paramref name="sink"/>; the
  /// stream must be decoded through every earlier file to reach a later one.
  /// </summary>
  /// <param name="stream">The archive.</param>
  /// <param name="wanted">Which entries to materialise; the others are decoded and dropped.</param>
  /// <param name="sink">Receives each wanted entry with its bytes.</param>
  public void Decode(Stream stream, Func<KgbEntry, bool> wanted, Action<KgbEntry, byte[]> sink) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(wanted);
    ArgumentNullException.ThrowIfNull(sink);
    var last = -1;
    for (var i = 0; i < this.Entries.Count; ++i)
      if (wanted(this.Entries[i]))
        last = i;
    if (last < 0)
      return;
    stream.Position = this.DataOffset;
    var buffered = new BufferedStream(stream, 1 << 16);
    var coder = KgbCoder.ForDecoding(this.Level, buffered);
    for (var i = 0; i <= last; ++i) {
      var entry = this.Entries[i];
      if (wanted(entry)) {
        var data = new byte[checked((int)entry.Size)];
        for (var j = 0; j < data.Length; ++j)
          data[j] = coder.DecodeByte();
        sink(entry, data);
      } else
        for (var j = 0L; j < entry.Size; ++j)
          coder.DecodeByte();
    }
  }

  /// <summary>Writes an archive of <paramref name="files"/> coded at memory level <paramref name="level"/>.</summary>
  /// <exception cref="ArgumentException">A name cannot be stored in a header line.</exception>
  public static void Write(Stream output, int level, IEnumerable<(string Name, byte[] Data)> files) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    if (level is < 0 or > 9)
      throw new ArgumentOutOfRangeException(nameof(level), "KGB memory levels are 0 to 9.");
    var list = files.ToList();
    var header = new StringBuilder();
    header.Append(CultureInfo.InvariantCulture, $"{Signature} -{level}\r\n");
    foreach (var (name, data) in list) {
      ValidateName(name);
      header.Append(CultureInfo.InvariantCulture, $"{data.Length}\t{name}\r\n");
    }
    var bytes = Encoding.UTF8.GetBytes(header.ToString());
    output.Write(bytes);
    output.Write([0x1A, 0x0C, 0x00]);

    var buffered = new BufferedStream(output, 1 << 16);
    var coder = KgbCoder.ForEncoding(level, buffered);
    foreach (var (_, data) in list)
      foreach (var b in data)
        coder.EncodeByte(b);
    coder.Flush();
    buffered.Flush();
  }

  private static void ValidateName(string name) {
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("KGB: an entry needs a name.");
    foreach (var ch in name)
      if (ch < ' ' && ch != '\t')
        throw new ArgumentException($"KGB: the name '{name}' holds a control character, which ends a header line.");
    if (name.Contains("../", StringComparison.Ordinal) || name.Contains("..\\", StringComparison.Ordinal))
      throw new ArgumentException($"KGB: the name '{name}' climbs out of the target directory; KGB refuses to extract it.");
  }

  /// <summary>A header line: bytes up to the first control byte other than tab; a CR also takes the byte after it.</summary>
  private static string ReadLine(Stream stream) {
    var bytes = new List<byte>();
    int c;
    while ((c = stream.ReadByte()) >= 32 || c == '\t')
      bytes.Add((byte)c);
    if (c == '\r')
      stream.ReadByte();
    return Encoding.UTF8.GetString([.. bytes]);
  }

  /// <summary>The decimal number at the start of <paramref name="line"/> (optional blanks and sign), 0 when there is none.</summary>
  private static long LeadingNumber(string line) {
    var i = 0;
    while (i < line.Length && line[i] is ' ' or '\t')
      ++i;
    var negative = false;
    if (i < line.Length && line[i] is '+' or '-')
      negative = line[i++] == '-';
    long value = 0;
    for (; i < line.Length && line[i] is >= '0' and <= '9'; ++i) {
      value = value * 10 + (line[i] - '0');
      if (value > int.MaxValue)
        throw new InvalidDataException($"KGB: size in header line '{line}' is out of range.");
    }
    return negative ? -value : value;
  }
}
