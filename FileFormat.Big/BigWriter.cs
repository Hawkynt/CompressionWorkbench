using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Big;

/// <summary>
/// Writes entries to an EA Games BIG archive (BIGF variant, big-endian offsets/sizes).
/// </summary>
public sealed class BigWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, byte[] Data)> _files = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="BigWriter"/> targeting the given stream.
  /// </summary>
  /// <param name="output">The stream to write the archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public BigWriter(Stream output, bool leaveOpen = false) {
    this._output = output ?? throw new ArgumentNullException(nameof(output));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file to the archive.
  /// </summary>
  /// <param name="path">The archive path, using forward or back slashes.</param>
  /// <param name="data">The file data.</param>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    if (this._finished)
      throw new InvalidOperationException("Cannot add files after Finish() has been called.");
    this._files.Add((path, data));
  }

  /// <summary>
  /// Finalises and writes the complete BIGF archive to the output stream.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // Compute the size of each directory entry:
    //   4 (offset) + 4 (size) + path bytes + 1 (null terminator)
    var dirEntryLengths = this._files
      .Select(f => 4 + 4 + Encoding.UTF8.GetByteCount(f.Path) + 1)
      .ToArray();

    var headerSize = 16; // magic(4) + totalSize(4) + numFiles(4) + headerSize(4)
    var directorySize = dirEntryLengths.Sum();
    var totalHeaderSize = (uint)(headerSize + directorySize);

    // Compute data offsets (from start of file)
    var dataOffsets = new uint[this._files.Count];
    var cursor = totalHeaderSize;
    for (var i = 0; i < this._files.Count; i++) {
      dataOffsets[i] = cursor;
      cursor += (uint)this._files[i].Data.Length;
    }

    var totalSize = cursor;

    // Write header (all big-endian)
    Span<byte> buf4 = stackalloc byte[4];

    // magic "BIGF"
    this._output.Write("BIGF"u8);

    // total file size (BE)
    BinaryPrimitives.WriteUInt32BigEndian(buf4, totalSize);
    this._output.Write(buf4);

    // num files (BE)
    BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)this._files.Count);
    this._output.Write(buf4);

    // header+directory size (BE)
    BinaryPrimitives.WriteUInt32BigEndian(buf4, totalHeaderSize);
    this._output.Write(buf4);

    // Write directory entries (big-endian offsets/sizes)
    for (var i = 0; i < this._files.Count; i++) {
      var (path, data) = this._files[i];

      BinaryPrimitives.WriteUInt32BigEndian(buf4, dataOffsets[i]);
      this._output.Write(buf4);

      BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)data.Length);
      this._output.Write(buf4);

      this._output.Write(Encoding.UTF8.GetBytes(path));
      this._output.WriteByte(0); // null terminator
    }

    // Write data blocks
    foreach (var (_, data) in this._files)
      this._output.Write(data);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._output.Dispose();
    }
  }
}
