using System.Text;

namespace FileFormat.Hog;

/// <summary>
/// Creates a Descent I/II HOG archive.
/// </summary>
public sealed class HogWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="HogWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the HOG archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public HogWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file to the archive.
  /// </summary>
  /// <param name="name">The file name (up to 13 characters; longer names are truncated).</param>
  /// <param name="data">The file data.</param>
  public void AddFile(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add files after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    // Truncate to 13 chars (12 + null terminator fits in 13-byte field)
    if (name.Length > 13)
      name = name[..13];

    this._files.Add((name, data));
  }

  /// <summary>
  /// Writes the HOG archive to the stream and finishes writing.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    // Write "DHF" magic
    this._stream.Write("DHF"u8);

    Span<byte> header = stackalloc byte[17]; // 13-byte name + 4-byte size

    foreach (var (name, data) in this._files) {
      header.Clear();

      // Write null-padded 13-byte filename
      var nameBytes = Encoding.ASCII.GetBytes(name);
      nameBytes.AsSpan().CopyTo(header[..13]);

      // Write 4-byte LE uint32 size
      BitConverter.TryWriteBytes(header[13..17], (uint)data.Length);

      this._stream.Write(header);
      this._stream.Write(data);
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
