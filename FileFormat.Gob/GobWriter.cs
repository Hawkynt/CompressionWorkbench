using System.Text;

namespace FileFormat.Gob;

/// <summary>
/// Creates a Lucasarts GOB v2 archive (Jedi Knight, Outlaws).
/// </summary>
public sealed class GobWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly uint _version;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="GobWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the GOB archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="version">GOB version field (default 0x14, Jedi Knight).</param>
  public GobWriter(Stream stream, bool leaveOpen = false, uint version = GobConstants.DefaultVersion) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._version = version;
  }

  /// <summary>
  /// Adds an entry to the archive. Names use backslash separators (e.g. "data\\test.bin").
  /// </summary>
  /// <param name="name">The relative entry path. Must be ≤ 127 ASCII bytes (128th byte is the null terminator).</param>
  /// <param name="data">The raw entry bytes (stored uncompressed).</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var byteCount = Encoding.ASCII.GetByteCount(name);
    if (byteCount > GobConstants.MaxNameLength)
      throw new ArgumentException(
        $"GOB entry name '{name}' is {byteCount} bytes; must be ≤ {GobConstants.MaxNameLength} bytes (128-byte field includes null terminator).",
        nameof(name));

    this._entries.Add((name, data));
  }

  /// <summary>
  /// Writes the archive to the stream and finishes writing.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var startPosition = this._stream.Position;

    // Write header with placeholder dir_offset; we backpatch once we know where
    // the directory landed. Streaming writers can't compute it up-front because
    // payloads are written sequentially before the directory.
    this._stream.Write(GobConstants.Magic);
    this._stream.Write(BitConverter.GetBytes(this._version));
    this._stream.Write(BitConverter.GetBytes(0u));

    var offsets = new long[this._entries.Count];
    for (var i = 0; i < this._entries.Count; ++i) {
      offsets[i] = this._stream.Position;
      var data = this._entries[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }

    var dirOffset = this._stream.Position;

    this._stream.Write(BitConverter.GetBytes((uint)this._entries.Count));

    Span<byte> entryBuf = stackalloc byte[GobConstants.DirectoryEntrySize];
    for (var i = 0; i < this._entries.Count; ++i) {
      entryBuf.Clear();
      var (name, data) = this._entries[i];

      BitConverter.TryWriteBytes(entryBuf[0..4], (uint)offsets[i]);
      BitConverter.TryWriteBytes(entryBuf[4..8], (uint)data.Length);
      WriteName(entryBuf[8..(8 + GobConstants.NameFieldSize)], name);

      this._stream.Write(entryBuf);
    }

    // Backpatch the directory offset at header offset+8.
    var endPosition = this._stream.Position;
    this._stream.Position = startPosition + 8;
    this._stream.Write(BitConverter.GetBytes((uint)dirOffset));
    this._stream.Position = endPosition;
  }

  private static void WriteName(Span<byte> destination, string name) {
    // destination is already zeroed by the caller's Clear(), so any unused tail
    // becomes the required null padding for the 128-byte fixed field.
    var written = Encoding.ASCII.GetBytes(name, destination);
    if (written < destination.Length)
      destination[written] = 0;
  }

  /// <inheritdoc />
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
