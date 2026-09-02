using System.Text;

namespace FileFormat.Wad;

/// <summary>
/// Creates an id Software WAD archive (Doom/Heretic/Hexen).
/// </summary>
public sealed class WadWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly bool _isIwad;
  private readonly List<(string Name, byte[] Data)> _lumps = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="WadWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the WAD archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="isIwad">If true, writes an IWAD header; otherwise writes a PWAD header.</param>
  public WadWriter(Stream stream, bool leaveOpen = false, bool isIwad = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._isIwad = isIwad;
  }

  /// <summary>
  /// Adds a lump with data.
  /// </summary>
  /// <param name="name">The lump name (up to 8 ASCII characters; auto-uppercased and truncated).</param>
  /// <param name="data">The lump data.</param>
  public void AddLump(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add lumps after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    this._lumps.Add((NormalizeName(name), data));
  }

  /// <summary>
  /// Adds a zero-size marker lump (e.g., "MAP01", "S_START", "S_END").
  /// </summary>
  /// <param name="name">The marker name (up to 8 ASCII characters; auto-uppercased and truncated).</param>
  public void AddMarker(string name) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add lumps after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);

    this._lumps.Add((NormalizeName(name), []));
  }

  /// <summary>
  /// Writes the WAD archive to the stream and finishes writing.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    // Write header placeholder
    var magic = this._isIwad ? WadConstants.MagicIwadString : WadConstants.MagicPwadString;
    this._stream.Write(Encoding.ASCII.GetBytes(magic));
    this._stream.Write(BitConverter.GetBytes(this._lumps.Count));
    this._stream.Write(BitConverter.GetBytes(0)); // directory offset placeholder

    // Write all lump data, recording offsets
    var offsets = new int[this._lumps.Count];
    for (var i = 0; i < this._lumps.Count; ++i) {
      offsets[i] = (int)this._stream.Position;
      var data = this._lumps[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }

    // Write directory
    var directoryOffset = (int)this._stream.Position;
    Span<byte> entryBuf = stackalloc byte[WadConstants.DirectoryEntrySize];

    for (var i = 0; i < this._lumps.Count; ++i) {
      entryBuf.Clear();

      BitConverter.TryWriteBytes(entryBuf[..4], offsets[i]);
      BitConverter.TryWriteBytes(entryBuf[4..8], this._lumps[i].Data.Length);
      WriteLumpName(entryBuf[8..16], this._lumps[i].Name);

      this._stream.Write(entryBuf);
    }

    // Seek back and update header with directory offset
    this._stream.Position = 8;
    this._stream.Write(BitConverter.GetBytes(directoryOffset));
    this._stream.Position = this._stream.Length;
  }

  private static string NormalizeName(string name) {
    if (name.Length > WadConstants.MaxLumpNameLength)
      name = name[..WadConstants.MaxLumpNameLength];

    return name.ToUpperInvariant();
  }

  private static void WriteLumpName(Span<byte> destination, string name) {
    var bytes = Encoding.ASCII.GetBytes(name);
    bytes.AsSpan().CopyTo(destination);
    // Remaining bytes are already zero from Clear()
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
