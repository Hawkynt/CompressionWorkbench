using System.Text;

namespace FileFormat.Sar;

/// <summary>
/// Reads entries from an NScripter SAR archive.
/// </summary>
/// <remarks>
/// SAR format:
/// <list type="bullet">
///   <item>Header: uint16 BE file count, uint32 BE data offset</item>
///   <item>Per entry: null-terminated filename, uint32 BE offset (relative to data start), uint32 BE file size</item>
///   <item>Data area: uncompressed file data starts at the data offset</item>
/// </list>
/// SAR is the uncompressed variant of NSA — no compression type field.
/// </remarks>
public sealed class SarReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the entries in this archive.</summary>
  public IReadOnlyList<SarEntry> Entries { get; }

  private readonly uint _dataOffset;

  /// <summary>
  /// Initializes a new <see cref="SarReader"/> from a stream containing a SAR archive.
  /// </summary>
  public SarReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    var fileCount = ReadUInt16BE();
    this._dataOffset = ReadUInt32BE();

    var entries = new List<SarEntry>(fileCount);
    for (var i = 0; i < fileCount; i++) {
      var name = ReadNullTerminatedString();
      var offset = ReadUInt32BE();
      var size = ReadUInt32BE();

      entries.Add(new SarEntry {
        Name = name,
        Offset = this._dataOffset + offset,
        Size = size,
      });
    }

    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the data for the given entry.
  /// </summary>
  public byte[] Extract(SarEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];

    this._stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    this._stream.ReadExactly(data);
    return data;
  }

  private ushort ReadUInt16BE() {
    Span<byte> buf = stackalloc byte[2];
    this._stream.ReadExactly(buf);
    return (ushort)((buf[0] << 8) | buf[1]);
  }

  private uint ReadUInt32BE() {
    Span<byte> buf = stackalloc byte[4];
    this._stream.ReadExactly(buf);
    return (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
  }

  private string ReadNullTerminatedString() {
    var bytes = new List<byte>();
    while (true) {
      var b = this._stream.ReadByte();
      if (b <= 0) break;
      bytes.Add((byte)b);
    }
    return Encoding.ASCII.GetString(bytes.ToArray());
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
