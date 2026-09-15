using System.Globalization;
using System.Text;

namespace FileFormat.Mtree;

/// <summary>Writes portable full-path mtree(5) manifests.</summary>
public sealed class MtreeWriter : IDisposable {
  private static readonly HashSet<string> StructuredKeywords = new(StringComparer.OrdinalIgnoreCase) {
    "type", "size", "mode", "uid", "gid", "time", "link", "contents",
  };

  private readonly StreamWriter _writer;
  private bool _disposed;

  /// <summary>Initializes a writer over <paramref name="stream"/>.</summary>
  public MtreeWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._writer = new StreamWriter(
      stream,
      new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
      bufferSize: 4096,
      leaveOpen: leaveOpen) {
      NewLine = "\n",
    };
    this._writer.WriteLine("#mtree");
  }

  /// <summary>Writes one manifest entry.</summary>
  public void WriteEntry(MtreeEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (string.IsNullOrWhiteSpace(entry.Path))
      throw new ArgumentException("mtree entries require a non-empty path.", nameof(entry));

    this._writer.Write("./");
    this._writer.Write(EncodeToken(entry.Path));
    this._writer.Write(" type=");
    this._writer.Write(TypeName(entry.Type));

    if (entry.Mode is { } mode) {
      this._writer.Write(" mode=");
      this._writer.Write(Convert.ToString(mode, 8));
    }
    if (entry.Uid is { } uid) {
      this._writer.Write(" uid=");
      this._writer.Write(uid.ToString(CultureInfo.InvariantCulture));
    }
    if (entry.Gid is { } gid) {
      this._writer.Write(" gid=");
      this._writer.Write(gid.ToString(CultureInfo.InvariantCulture));
    }
    if (entry.Size is { } size) {
      if (size < 0)
        throw new ArgumentOutOfRangeException(nameof(entry), "mtree entry size cannot be negative.");
      this._writer.Write(" size=");
      this._writer.Write(size.ToString(CultureInfo.InvariantCulture));
    }
    if (entry.ModificationTime is { } time) {
      this._writer.Write(" time=");
      this._writer.Write(FormatTime(time));
    }
    if (entry.LinkTarget is { } link) {
      this._writer.Write(" link=");
      this._writer.Write(EncodeToken(link));
    }
    if (entry.ContentsPath is { } contents) {
      this._writer.Write(" contents=");
      this._writer.Write(EncodeToken(contents));
    }

    foreach (var (key, value) in entry.Keywords.OrderBy(x => x.Key, StringComparer.Ordinal)) {
      if (StructuredKeywords.Contains(key))
        continue;
      this._writer.Write(' ');
      this._writer.Write(key);
      if (value != null) {
        this._writer.Write('=');
        this._writer.Write(EncodeToken(MtreeReader.DecodeToken(value)));
      }
    }

    this._writer.WriteLine();
  }

  /// <summary>Flushes the manifest to its underlying stream.</summary>
  public void Flush() => this._writer.Flush();

  internal static string EncodeToken(string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    var result = new StringBuilder(bytes.Length);
    foreach (var valueByte in bytes) {
      if (valueByte is >= 0x21 and <= 0x7E && valueByte is not (byte)'\\' and not (byte)'#') {
        result.Append((char)valueByte);
        continue;
      }

      result.Append('\\');
      result.Append((char)('0' + ((valueByte >> 6) & 0x07)));
      result.Append((char)('0' + ((valueByte >> 3) & 0x07)));
      result.Append((char)('0' + (valueByte & 0x07)));
    }
    return result.ToString();
  }

  private static string TypeName(MtreeEntryType type) => type switch {
    MtreeEntryType.Directory => "dir",
    MtreeEntryType.Link => "link",
    MtreeEntryType.Block => "block",
    MtreeEntryType.Character => "char",
    MtreeEntryType.Fifo => "fifo",
    MtreeEntryType.Socket => "socket",
    _ => "file",
  };

  private static string FormatTime(DateTimeOffset value) {
    var seconds = value.ToUnixTimeSeconds();
    var epochSecond = DateTimeOffset.FromUnixTimeSeconds(seconds);
    var ticks = value.UtcTicks - epochSecond.UtcTicks;
    if (ticks < 0) {
      --seconds;
      ticks += TimeSpan.TicksPerSecond;
    }
    var nanoseconds = ticks * 100;
    return string.Create(CultureInfo.InvariantCulture, $"{seconds}.{nanoseconds:000000000}");
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    this._writer.Dispose();
  }
}
