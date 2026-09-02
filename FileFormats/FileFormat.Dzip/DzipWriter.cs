using System.Text;

namespace FileFormat.Dzip;

/// <summary>
/// Creates a Bloodlines DZIP v2 archive in WORM mode. All entries are written stored
/// (compression flag = 0); LZSS compression is read-only.
/// </summary>
public sealed class DzipWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="DzipWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the DZIP archive to. Must be writable and seekable.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public DzipWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));

    this._stream = stream;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a stored entry to the archive.
  /// </summary>
  /// <param name="name">The forward-slash separated entry path (max 255 ASCII chars).</param>
  /// <param name="data">The raw entry data; written stored (compression flag = 0).</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var normalized = NormalizeName(name);
    var byteLen = Encoding.ASCII.GetByteCount(normalized);
    if (byteLen > DzipConstants.MaxPathLength)
      throw new ArgumentException($"Entry path exceeds {DzipConstants.MaxPathLength} bytes: '{normalized}' ({byteLen} bytes).", nameof(name));

    this._entries.Add((normalized, data));
  }

  /// <summary>
  /// Writes the archive to the stream and finalizes it.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    Span<byte> header = stackalloc byte[DzipConstants.HeaderSize];
    DzipConstants.MagicBytes.CopyTo(header[..4]);
    BitConverter.TryWriteBytes(header[4..8], DzipConstants.SupportedVersion);
    BitConverter.TryWriteBytes(header[8..12], (uint)this._entries.Count);
    BitConverter.TryWriteBytes(header[12..16], 0u);
    this._stream.Write(header);

    var dataOffsets = new long[this._entries.Count];
    for (var i = 0; i < this._entries.Count; ++i) {
      dataOffsets[i] = this._stream.Position;
      var data = this._entries[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }

    var tocOffset = this._stream.Position;

    Span<byte> tail = stackalloc byte[13];
    for (var i = 0; i < this._entries.Count; ++i) {
      var (name, data) = this._entries[i];
      var nameBytes = Encoding.ASCII.GetBytes(name);

      this._stream.WriteByte((byte)nameBytes.Length);
      this._stream.Write(nameBytes);

      BitConverter.TryWriteBytes(tail[0..4], (uint)dataOffsets[i]);
      BitConverter.TryWriteBytes(tail[4..8], (uint)data.Length);
      BitConverter.TryWriteBytes(tail[8..12], (uint)data.Length);
      tail[12] = 0;
      this._stream.Write(tail);
    }

    this._stream.Position = 12;
    Span<byte> tocBuf = stackalloc byte[4];
    BitConverter.TryWriteBytes(tocBuf, (uint)tocOffset);
    this._stream.Write(tocBuf);
    this._stream.Position = this._stream.Length;
  }

  private static string NormalizeName(string name)
    => name.Replace('\\', '/').TrimStart('/');

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
