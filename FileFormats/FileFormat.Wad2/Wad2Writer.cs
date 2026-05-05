using System.Text;

namespace FileFormat.Wad2;

/// <summary>
/// Creates a Quake/Half-Life WAD2 or WAD3 texture archive.
/// </summary>
public sealed class Wad2Writer : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly bool _isWad3;
  private readonly List<(string Name, byte[] Data, byte Type)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="Wad2Writer"/>.
  /// </summary>
  /// <param name="stream">The stream to write the WAD archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="isWad3">If true (default), writes a WAD3 header (Half-Life); otherwise writes WAD2 (Quake).</param>
  public Wad2Writer(Stream stream, bool leaveOpen = false, bool isWad3 = true) {
    this._stream   = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._isWad3    = isWad3;
  }

  /// <summary>
  /// Adds an entry to the archive.
  /// </summary>
  /// <param name="name">The entry name (up to 16 ASCII characters; truncated if longer).</param>
  /// <param name="data">The raw entry data.</param>
  /// <param name="type">The entry type byte (default 0x43 = texture).</param>
  public void AddEntry(string name, byte[] data, byte type = Wad2Constants.TypeTexture) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    this._entries.Add((NormalizeName(name), data, type));
  }

  /// <summary>
  /// Writes the WAD archive to the stream and finishes writing.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    var magic = this._isWad3 ? Wad2Constants.MagicWad3String : Wad2Constants.MagicWad2String;

    // Write placeholder header (12 bytes)
    this._stream.Write(Encoding.ASCII.GetBytes(magic));
    this._stream.Write(BitConverter.GetBytes((uint)this._entries.Count));
    this._stream.Write(BitConverter.GetBytes(0u)); // dirOffset placeholder

    // Write all entry data, recording offsets
    var dataOffsets = new int[this._entries.Count];
    for (var i = 0; i < this._entries.Count; ++i) {
      dataOffsets[i] = (int)this._stream.Position;
      var data = this._entries[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }

    // Write directory at end
    var dirOffset = (int)this._stream.Position;
    Span<byte> entryBuf = stackalloc byte[Wad2Constants.DirectoryEntrySize];

    for (var i = 0; i < this._entries.Count; ++i) {
      entryBuf.Clear();

      var (name, data, type) = this._entries[i];

      BitConverter.TryWriteBytes(entryBuf[0..4],  (uint)dataOffsets[i]);
      BitConverter.TryWriteBytes(entryBuf[4..8],  (uint)data.Length);   // diskSize
      BitConverter.TryWriteBytes(entryBuf[8..12], (uint)data.Length);   // uncompressed size
      entryBuf[12] = type;
      entryBuf[13] = 0; // compression = none
      // entryBuf[14..16] = 0 — padding (already cleared)
      WriteEntryName(entryBuf[16..32], name);

      this._stream.Write(entryBuf);
    }

    // Seek back to fix the dirOffset in the header
    this._stream.Position = 8;
    this._stream.Write(BitConverter.GetBytes((uint)dirOffset));
    this._stream.Position = this._stream.Length;
  }

  private static string NormalizeName(string name) {
    if (name.Length > Wad2Constants.MaxNameLength)
      name = name[..Wad2Constants.MaxNameLength];
    return name;
  }

  private static void WriteEntryName(Span<byte> destination, string name) {
    var bytes = Encoding.ASCII.GetBytes(name);
    bytes.AsSpan().CopyTo(destination);
    // Remaining bytes are already zeroed by Clear()
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
