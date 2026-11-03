using System.Buffers.Binary;

namespace FileFormat.Dms;

/// <summary>
/// Writes an Amiga DMS archive with stored (mode 0) or Simple RLE (mode 1) tracks. Both modes are
/// stateless, so every track carries the reset flag of a standalone track, and the header's
/// totals, track range and CRC are patched on dispose.
/// </summary>
public sealed class DmsWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly long _headerPosition;
  private int _lowTrack = int.MaxValue, _highTrack = -1;
  private uint _packed, _unpacked;
  private int _maxMode;
  private bool _disposed;

  public DmsWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._headerPosition = stream.Position;
    stream.Write(new byte[DmsConstants.FileHeaderSize]);
  }

  /// <summary>Appends one track. <paramref name="compressionMode"/> 0 stores, 1 RLE-codes (falling back to stored when RLE does not help).</summary>
  public void WriteTrack(int trackNumber, ReadOnlySpan<byte> data, int compressionMode = 0) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (compressionMode is not (DmsConstants.ModeNone or DmsConstants.ModeSimple))
      throw new NotSupportedException($"DMS writer supports modes 0 (stored) and 1 (RLE), not {compressionMode}.");
    if (data.Length > ushort.MaxValue)
      throw new ArgumentException("A DMS track holds at most 65 535 bytes.", nameof(data));
    if (trackNumber is < short.MinValue or > short.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(trackNumber));

    var payload = data.ToArray();
    var mode = DmsConstants.ModeNone;
    if (compressionMode == DmsConstants.ModeSimple && EncodeRle(data) is { } rle && rle.Length < data.Length) {
      payload = rle;
      mode = DmsConstants.ModeSimple;
    }

    Span<byte> header = stackalloc byte[DmsConstants.TrackHeaderSize];
    DmsTrackHeader.Write(header, (short)trackNumber, (ushort)payload.Length, (ushort)data.Length, (ushort)data.Length,
      0, (byte)mode, DmsCrc.Sum(data), DmsCrc.Compute(payload));
    this._stream.Write(header);
    this._stream.Write(payload);

    this._packed += (uint)payload.Length;
    this._unpacked += (uint)data.Length;
    this._maxMode = Math.Max(this._maxMode, mode);
    if (trackNumber is >= 0 and < DmsConstants.TrackDiz) {
      this._lowTrack = Math.Min(this._lowTrack, trackNumber);
      this._highTrack = Math.Max(this._highTrack, trackNumber);
    }
  }

  /// <summary>Writes a disk image as consecutive 11 264-byte cylinders (the last may be shorter).</summary>
  public void WriteDisk(ReadOnlySpan<byte> image, int compressionMode = 0) {
    var cylinder = 0;
    for (var offset = 0; offset < image.Length; offset += DmsConstants.CylinderSize, ++cylinder)
      this.WriteTrack(cylinder, image.Slice(offset, Math.Min(DmsConstants.CylinderSize, image.Length - offset)), compressionMode);
  }

  /// <summary>
  /// RLE as DMS decodes it: <c>90 00</c> is a literal 0x90; <c>90 n b</c> repeats b n times
  /// (3 ≤ n ≤ 254); <c>90 FF hi lo b</c> repeats b for a 16-bit count.
  /// </summary>
  internal static byte[] EncodeRle(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream(data.Length);
    for (var i = 0; i < data.Length;) {
      var b = data[i];
      var run = 1;
      while (i + run < data.Length && data[i + run] == b && run < ushort.MaxValue) ++run;
      if (run > 3 || (b == 0x90 && run > 1)) {
        ms.WriteByte(0x90);
        if (run < 0xFF) {
          ms.WriteByte((byte)run);
          ms.WriteByte(b);
        } else {
          ms.WriteByte(0xFF);
          ms.WriteByte(b);
          ms.WriteByte((byte)(run >> 8));
          ms.WriteByte((byte)run);
        }
        i += run;
        continue;
      }
      if (b == 0x90) {
        ms.WriteByte(0x90);
        ms.WriteByte(0x00);
      } else
        ms.WriteByte(b);
      ++i;
    }
    return ms.ToArray();
  }

  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    var end = this._stream.Position;
    var header = new DmsHeader(
      Id: 0x2050524F, // " PRO"
      InfoFlags: 0, Date: 0,
      LowTrack: (ushort)(this._highTrack < 0 ? 0 : this._lowTrack), HighTrack: (ushort)Math.Max(this._highTrack, 0),
      PackedSize: this._packed, UnpackedSize: this._unpacked,
      OsVersion: 0, OsRevision: 0, Cpu: 0, Fpu: 0, Machine: 1, DiskType2: 0, CpuSpeed: 0, CreationTime: 0,
      CreatorVersion: 111, NeededVersion: 111, DiskType: 0, CompressionMode: (ushort)this._maxMode);
    Span<byte> buffer = stackalloc byte[DmsConstants.FileHeaderSize];
    header.Write(buffer);
    this._stream.Position = this._headerPosition;
    this._stream.Write(buffer);
    this._stream.Position = end;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
