namespace FileSystem.HfsPlus;

/// <summary>
/// A read-only view of a volume with its journal's writes laid over it: what the volume holds
/// once the journal is replayed, without writing to the image.
/// </summary>
internal sealed class HfsPlusReplayedStream : Stream {
  private readonly Stream _volume;
  private readonly List<(long Offset, byte[] Data)> _writes;
  private long _position;

  /// <param name="volume">The volume as stored; left open.</param>
  /// <param name="writes">The replay's writes, later ones winning where they overlap.</param>
  internal HfsPlusReplayedStream(Stream volume, List<(long Offset, byte[] Data)> writes) {
    this._volume = volume;
    this._writes = writes;
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => this._volume.Length;

  public override long Position {
    get => this._position;
    set => this._position = value;
  }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    if (this._position >= this.Length) return 0;
    var count = (int)Math.Min(buffer.Length, this.Length - this._position);
    this._volume.Position = this._position;
    var target = buffer[..count];
    var done = 0;
    while (done < count) {
      var read = this._volume.Read(target[done..]);
      if (read == 0) break;
      done += read;
    }
    target[done..].Clear();
    foreach (var (at, data) in this._writes) {
      var from = Math.Max(at, this._position);
      var to = Math.Min(at + data.Length, this._position + count);
      if (from < to) data.AsSpan((int)(from - at), (int)(to - from)).CopyTo(target[(int)(from - this._position)..]);
    }
    this._position += count;
    return count;
  }

  public override long Seek(long offset, SeekOrigin origin) => this._position = origin switch {
    SeekOrigin.Begin => offset,
    SeekOrigin.Current => this._position + offset,
    _ => this.Length + offset,
  };

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
