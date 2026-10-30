using Compression.Core.Dictionary.Ha;

namespace FileFormat.Ha;

/// <summary>
/// Writes an HA archive as HA 0.999 does: version-2 records, MS-DOS machine information, HSC
/// members (stored as CPY when HSC does not shrink them), 0xFF-separated paths.
/// </summary>
public sealed class HaWriter : IDisposable {
  private const byte DosMachine = 1;
  private const byte DosArchiveAttribute = 0x20;
  private const byte DosDirectoryAttribute = 0x10;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly long _start;
  private int _count;
  private bool _finished;

  /// <summary>Starts an archive at the stream's current position.</summary>
  /// <param name="stream">A writable, seekable stream.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  public HaWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek || !stream.CanWrite)
      throw new ArgumentException("Stream must be writable and seekable.", nameof(stream));
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    this._start = stream.Position;
    stream.Write("HA\0\0"u8);
  }

  /// <summary>Adds a file.</summary>
  /// <param name="name">The member name; '/' or '\' separate directories.</param>
  /// <param name="data">The member's bytes.</param>
  /// <param name="modifiedUtc">The modification time (now when null).</param>
  public void AddFile(string name, ReadOnlySpan<byte> data, DateTime? modifiedUtc = null) {
    this._CheckRoom();
    this._stream.Write(BuildFileRecord(name, data, modifiedUtc));
    ++this._count;
  }

  private void _CheckRoom() {
    if (this._count == ushort.MaxValue)
      throw new NotSupportedException("An HA archive holds at most 65535 members.");
  }

  /// <summary>Adds a directory record.</summary>
  /// <param name="name">The directory's name; '/' or '\' separate its parents.</param>
  /// <param name="modifiedUtc">The modification time (now when null).</param>
  public void AddDirectory(string name, DateTime? modifiedUtc = null) {
    this._CheckRoom();
    this._stream.Write(BuildRecord(HaEntry.MethodDirectory, name.TrimEnd('/', '\\'), 0, 0, [], modifiedUtc, DosDirectoryAttribute));
    ++this._count;
  }

  /// <summary>Builds one file record: HSC when it shrinks the data, otherwise CPY.</summary>
  internal static byte[] BuildFileRecord(string name, ReadOnlySpan<byte> data, DateTime? modifiedUtc) {
    var crc = Compression.Core.Checksums.Crc32.Compute(data);
    if (data.Length == 0)
      return BuildRecord(HaEntry.MethodCpy, name, 0, crc, [], modifiedUtc, DosArchiveAttribute);

    var packed = HaHsc.Encode(data);
    return packed.Length < data.Length
      ? BuildRecord(HaEntry.MethodHsc, name, (uint)data.Length, crc, packed, modifiedUtc, DosArchiveAttribute)
      : BuildRecord(HaEntry.MethodCpy, name, (uint)data.Length, crc, data, modifiedUtc, DosArchiveAttribute);
  }

  private static byte[] BuildRecord(int type, string fullName, uint originalLength, uint crc, ReadOnlySpan<byte> payload,
    DateTime? modifiedUtc, byte attribute) {
    var parts = fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
      throw new ArgumentException("An HA member needs a name.", nameof(fullName));

    using var ms = new MemoryStream();
    ms.WriteByte((byte)((HaReader.Version << 4) | type));
    Span<byte> word = stackalloc byte[4];
    var seconds = (uint)Math.Clamp(((modifiedUtc ?? DateTime.UtcNow).ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds, 0, uint.MaxValue);
    foreach (var value in (ReadOnlySpan<uint>)[(uint)payload.Length, originalLength, crc, seconds]) {
      BitConverter.TryWriteBytes(word, value);
      ms.Write(word);
    }

    foreach (var dir in parts[..^1]) {
      ms.Write(HaReader.NameEncoding.GetBytes(dir));
      ms.WriteByte(HaReader.PathSeparator);
    }

    ms.WriteByte(0);
    ms.Write(HaReader.NameEncoding.GetBytes(parts[^1]));
    ms.WriteByte(0);
    ms.Write([2, DosMachine, attribute]);
    ms.Write(payload);
    return ms.ToArray();
  }

  /// <summary>Writes the member count into the archive header.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;
    var end = this._stream.Position;
    this._stream.Position = this._start + 2;
    this._stream.Write([(byte)this._count, (byte)(this._count >> 8)]);
    this._stream.Position = end;
    this._stream.Flush();
  }

  /// <inheritdoc />
  public void Dispose() {
    this.Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
