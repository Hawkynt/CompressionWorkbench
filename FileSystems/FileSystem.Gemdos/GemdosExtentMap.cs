#pragma warning disable CS1591
namespace FileSystem.Gemdos;

/// <summary>
/// On-disk layout walker for GEMDOS images. Delegates to FAT12's extent map
/// after re-presenting the GEMDOS jump byte (0x60) as MS-DOS's (0xEB) so the
/// FAT walker accepts the boot sector.
/// </summary>
public static class GemdosExtentMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static System.Collections.Generic.IEnumerable<Compression.Registry.DefragBlockInfo> Enumerate(
      System.IO.Stream image) {
    System.ArgumentNullException.ThrowIfNull(image);
    // Sniff jump byte and present a patched stream view if needed.
    image.Position = 0;
    var first = image.ReadByte();
    if (first < 0) yield break;
    image.Position = 0;
    System.IO.Stream view = first == GemdosBpb.GemdosJump
      ? new JumpPatchStream(image)
      : image;
    foreach (var ext in FileSystem.Fat.FatExtentMap.Enumerate(view))
      yield return ext;
  }

  /// <summary>
  /// Read-through wrapper that replaces byte 0 with <c>0xEB</c>. All other
  /// reads pass straight through to the underlying GEMDOS image — no copy.
  /// Position / length / seek delegate. Disposing this view does NOT dispose
  /// the underlying stream (the caller owns its lifetime).
  /// </summary>
  private sealed class JumpPatchStream : System.IO.Stream {

    private readonly System.IO.Stream _inner;
    public JumpPatchStream(System.IO.Stream inner) { _inner = inner; _inner.Position = 0; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) {
      var startedAtZero = _inner.Position == 0;
      var n = _inner.Read(buffer, offset, count);
      if (startedAtZero && n > 0 && buffer[offset] == GemdosBpb.GemdosJump)
        buffer[offset] = 0xEB;
      return n;
    }

    public override int Read(System.Span<byte> buffer) {
      var startedAtZero = _inner.Position == 0;
      var n = _inner.Read(buffer);
      if (startedAtZero && n > 0 && buffer[0] == GemdosBpb.GemdosJump)
        buffer[0] = 0xEB;
      return n;
    }

    public override long Seek(long offset, System.IO.SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() { /* read-only view */ }
    public override void SetLength(long value) => throw new System.NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
  }
}
