using System.Buffers.Binary;

namespace FileFormat.Daa;

/// <summary>
/// The volumes of a split DAA image joined into one read-only stream (docs/DAA-ON-DISK.md, "Split
/// images"): the whole first volume, then each later volume from the data start its 100-byte
/// <c>"DAA VOL"</c> header names. Offsets are those of the chunk table.
/// </summary>
internal sealed class DaaVolumeStream : Stream {
  /// <summary>Length of a later volume's header.</summary>
  public const int VolumeHeaderSize = 0x64;

  private readonly List<(Stream Stream, long Start, long Length, bool Owned)> _parts = [];
  private long _length;
  private long _position;

  /// <summary>Gets the number of volumes joined.</summary>
  public int VolumeCount => this._parts.Count;

  private DaaVolumeStream() { }

  /// <summary>Joins <paramref name="first"/> with volumes 2, 3, … until their file sizes add up to <paramref name="totalSize"/>.</summary>
  /// <exception cref="InvalidDataException">A volume is missing, is not a DAA volume, or the sizes do not add up.</exception>
  public static DaaVolumeStream Open(Stream first, long totalSize, Func<int, Stream?> openVolume) {
    var joined = new DaaVolumeStream();
    joined._parts.Add((first, 0, first.Length, false));
    joined._length = first.Length;
    var fileSizes = first.Length;
    try {
      for (var number = 2; fileSizes < totalSize; ++number) {
        var volume = openVolume(number) ?? throw new InvalidDataException($"DAA: volume {number} of the split image is missing.");
        joined._parts.Add((volume, 0, 0, true));
        var header = new byte[VolumeHeaderSize];
        volume.Position = 0;
        if (volume.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
            || !(header.AsSpan(0, 16).SequenceEqual("DAA VOL\0\0\0\0\0\0\0\0\0"u8) || header.AsSpan(0, 16).SequenceEqual("GBI VOL\0\0\0\0\0\0\0\0\0"u8)))
          throw new InvalidDataException($"DAA: volume {number} has no \"DAA VOL\" or \"GBI VOL\" header.");
        var start = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x10));
        if (start < VolumeHeaderSize || start > volume.Length)
          throw new InvalidDataException($"DAA: volume {number} names an invalid data start.");
        joined._parts[^1] = (volume, start, volume.Length - start, true);
        joined._length += volume.Length - start;
        fileSizes += volume.Length;
      }
      if (fileSizes != totalSize)
        throw new InvalidDataException($"DAA: the volumes add up to {fileSizes} bytes, the header says {totalSize}.");
      return joined;
    } catch {
      joined.Dispose();
      throw;
    }
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => this._length;

  public override long Position {
    get => this._position;
    set => this._position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
  }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var total = 0;
    var logical = 0L;
    foreach (var (stream, start, length, _) in this._parts) {
      if (buffer.IsEmpty)
        break;
      if (this._position < logical + length) {
        var within = this._position - logical;
        stream.Position = start + within;
        var n = stream.Read(buffer[..(int)Math.Min(buffer.Length, length - within)]);
        if (n <= 0)
          break;
        buffer = buffer[n..];
        this._position += n;
        total += n;
        if (this._position < logical + length)
          return total; // a short read inside one volume: let the caller ask again
      }
      logical += length;
    }
    return total;
  }

  public override long Seek(long offset, SeekOrigin origin) => this.Position = origin switch {
    SeekOrigin.Begin => offset,
    SeekOrigin.Current => this._position + offset,
    _ => this._length + offset,
  };

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  protected override void Dispose(bool disposing) {
    if (disposing)
      foreach (var (stream, _, _, owned) in this._parts)
        if (owned)
          stream.Dispose();
    base.Dispose(disposing);
  }
}
