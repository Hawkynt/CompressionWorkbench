#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FileFormat.Asar;

/// <summary>
/// Reader for Electron <c>.asar</c> archives. The archive begins with a
/// Chromium <c>Pickle</c>-wrapped header:
/// <list type="number">
///   <item>a size pickle — <c>uint32 = 4</c> followed by <c>uint32 = headerBufferLength</c>;</item>
///   <item>a header pickle — <c>uint32 = payloadSize</c>, <c>uint32 = jsonLength</c>,
///   then a UTF-8 JSON directory tree padded to a 4-byte boundary.</item>
/// </list>
/// File bytes are concatenated immediately after the header; each file node's
/// <c>offset</c> is a decimal string relative to the end of the header.
/// </summary>
public sealed class AsarReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;

  /// <summary>Absolute byte offset where the concatenated file data begins.</summary>
  public long DataStart { get; }

  /// <summary>All files and directories declared in the header, in tree order.</summary>
  public IReadOnlyList<AsarEntry> Entries { get; }

  /// <summary>
  /// Initializes a new instance of <see cref="AsarReader"/>.
  /// </summary>
  public AsarReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    if (stream.CanSeek) stream.Position = 0;

    var sizeField = ReadUInt32(stream);
    if (sizeField != 4)
      throw new InvalidDataException(
        $"Asar: expected size-pickle prelude 0x00000004, got 0x{sizeField:X8}.");
    var headerBufLen = ReadUInt32(stream);
    _ = ReadUInt32(stream);              // header pickle payload size (= headerBufLen - 4)
    var jsonLen = ReadUInt32(stream);
    if (jsonLen > headerBufLen)
      throw new InvalidDataException("Asar: JSON length exceeds header buffer.");
    var jsonBytes = ReadExactly(stream, (int)jsonLen);

    // The header on disk occupies 8 (size pickle) + headerBufLen bytes; the
    // JSON is padded to a 4-byte boundary, so file data starts after that pad.
    this.DataStart = 8 + headerBufLen;

    var root = JsonNode.Parse(Encoding.UTF8.GetString(jsonBytes))
      ?? throw new InvalidDataException("Asar: header JSON is null.");
    var files = root["files"]?.AsObject()
      ?? throw new InvalidDataException("Asar: header JSON has no 'files' object.");

    var entries = new List<AsarEntry>();
    Walk(string.Empty, files, entries);
    this.Entries = entries;
  }

  /// <summary>Reads a file entry's raw bytes from the data region.</summary>
  public byte[] ReadData(AsarEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (!this._stream.CanSeek)
      throw new NotSupportedException("Asar: ReadData requires a seekable stream.");
    this._stream.Position = this.DataStart + entry.Offset;
    return ReadExactly(this._stream, checked((int)entry.Size));
  }

  private static void Walk(string prefix, JsonObject files, List<AsarEntry> entries) {
    foreach (var (name, valueNode) in files) {
      if (valueNode is not JsonObject node) continue;
      var path = prefix.Length == 0 ? name : prefix + "/" + name;

      if (node["files"] is JsonObject sub) {
        entries.Add(new AsarEntry(path, 0, 0, Executable: false, IsDirectory: true));
        Walk(path, sub, entries);
        continue;
      }

      // Skip symlinks and externally-unpacked files — no in-archive bytes.
      if (node["link"] != null || node["unpacked"]?.GetValue<bool>() == true) continue;
      if (node["offset"] is not { } offsetNode) continue;

      var offset = long.Parse(offsetNode.GetValue<string>(), CultureInfo.InvariantCulture);
      var size = node["size"]?.GetValue<long>() ?? 0;
      var executable = node["executable"]?.GetValue<bool>() ?? false;
      entries.Add(new AsarEntry(path, offset, size, executable, IsDirectory: false));
    }
  }

  private static uint ReadUInt32(Stream s) {
    Span<byte> buf = stackalloc byte[4];
    ReadExactly(s, buf);
    return BinaryPrimitives.ReadUInt32LittleEndian(buf);
  }

  private static byte[] ReadExactly(Stream s, int count) {
    var buf = new byte[count];
    ReadExactly(s, buf);
    return buf;
  }

  private static void ReadExactly(Stream s, Span<byte> buf) {
    var total = 0;
    while (total < buf.Length) {
      var read = s.Read(buf[total..]);
      if (read <= 0) throw new EndOfStreamException("Asar: unexpected end of stream.");
      total += read;
    }
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() {
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
