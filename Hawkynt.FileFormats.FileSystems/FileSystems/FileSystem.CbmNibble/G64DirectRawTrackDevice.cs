#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.CbmNibble;

/// <summary>
/// G64 track device for mounted filesystems. Existing track-table entries are
/// replaced without compacting unrelated tracks: the new variable-length record
/// is appended first, flushed, then its existing offset-table entry is retargeted.
/// Old records become unreachable slack for the explicit archive defragmenter.
/// Growing the offset/speed tables is intentionally not supported here.
/// </summary>
internal sealed class G64DirectRawTrackDevice : IRawTrackDevice {
  private const int HeaderSize = 12;
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly Dictionary<int, CbmNibbleReader.Track> _tracks;
  private readonly int _trackCount;
  private int _maxTrackSize;
  private bool _disposed;

  public G64DirectRawTrackDevice(Stream stream, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("G64 track access requires a readable, seekable stream.", nameof(stream));
    if (writable && !stream.CanWrite)
      throw new ArgumentException("Writable G64 track access requires a writable stream.", nameof(stream));

    _stream = stream;
    _leaveOpen = leaveOpen;
    var parsed = CbmNibbleEntries.ReadImage(stream, "image.g64");
    var variable = parsed.Tracks.FirstOrDefault(track => track.SpeedZone > 3);
    if (writable && variable != null)
      throw new NotSupportedException(
        $"G64 half-track {variable.Index} uses variable-speed map pointer 0x{variable.SpeedZone:X8}; writable sector projection requires modeled speed-map blocks.");

    CanWrite = writable;
    _trackCount = parsed.TrackCount;
    _maxTrackSize = parsed.MaxTrackSize;
    _tracks = Enumerable.Range(0, _trackCount).ToDictionary(
      index => index,
      index => parsed.Tracks.FirstOrDefault(track => track.Index == index)
        ?? new CbmNibbleReader.Track(index, [], CbmNibbleEntries.DefaultSpeedZone(index)));
  }

  public int TrackCount => _trackCount;
  public bool CanWrite { get; }

  public IReadOnlyList<RawTrackInfo> EnumerateTracks() {
    ThrowIfDisposed();
    return Enumerable.Range(0, _trackCount)
      .Select(index => {
        var track = _tracks[index];
        return new RawTrackInfo(index, track.Data.LongLength, track.SpeedZone, track.Data.Length != 0);
      })
      .ToArray();
  }

  public int ReadTrack(int index, Span<byte> destination) {
    ThrowIfDisposed();
    ValidateIndex(index);
    var track = _tracks[index];
    if (track.Data.Length == 0) return 0;
    if (destination.Length < track.Data.Length)
      throw new ArgumentException($"Destination is {destination.Length} bytes; track needs {track.Data.Length}.", nameof(destination));
    track.Data.CopyTo(destination);
    return track.Data.Length;
  }

  public void WriteTrack(int index, ReadOnlySpan<byte> source, uint? encodingParameter = null) {
    ThrowIfDisposed();
    EnsureWritable();
    ValidateIndex(index);
    if (source.Length == 0) {
      ClearTrack(index);
      return;
    }
    if (source.Length > ushort.MaxValue)
      throw new NotSupportedException("G64's track length field is 16-bit.");

    var old = _tracks[index];
    var speed = encodingParameter ?? old.SpeedZone;
    if (speed > 3)
      throw new NotSupportedException("Pointer-based G64 variable-speed maps are not writable yet.");
    if (old.Data.Length != 0 && speed != old.SpeedZone)
      throw new NotSupportedException(
        "Mounted G64 writes keep an existing track's speed zone stable; change it through the offline track editor instead.");

    var recordOffset = _stream.Length;
    var recordEnd = checked(recordOffset + 2L + source.Length);
    if (recordOffset > uint.MaxValue || recordEnd > uint.MaxValue)
      throw new IOException("G64 append would exceed its 32-bit track-offset address space.");

    Span<byte> half = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(half, (ushort)source.Length);
    _stream.Position = recordOffset;
    _stream.Write(half);
    _stream.Write(source);
    _stream.Flush();

    if (source.Length > _maxTrackSize) {
      _maxTrackSize = source.Length;
      _stream.Position = 10;
      BinaryPrimitives.WriteUInt16LittleEndian(half, (ushort)_maxTrackSize);
      _stream.Write(half);
      _stream.Flush();
    }

    Span<byte> word = stackalloc byte[4];
    if (old.Data.Length == 0) {
      BinaryPrimitives.WriteUInt32LittleEndian(word, speed);
      _stream.Position = SpeedTableOffset(index);
      _stream.Write(word);
      _stream.Flush();
    }

    // Publication point: until this 32-bit table entry changes, readers still
    // select the old complete record. The appended record is harmless slack.
    BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)recordOffset);
    _stream.Position = OffsetTableOffset(index);
    _stream.Write(word);
    _stream.Flush();

    _tracks[index] = new CbmNibbleReader.Track(
      index, source.ToArray(), speed, recordOffset, 2L + source.Length);
  }

  public void ClearTrack(int index) {
    ThrowIfDisposed();
    EnsureWritable();
    ValidateIndex(index);
    Span<byte> zero = stackalloc byte[4];
    _stream.Position = OffsetTableOffset(index);
    _stream.Write(zero);
    _stream.Flush();
    var old = _tracks[index];
    _tracks[index] = new CbmNibbleReader.Track(index, [], old.SpeedZone);
  }

  public void Flush() {
    ThrowIfDisposed();
    _stream.Flush();
  }

  public void Dispose() {
    if (_disposed) return;
    if (CanWrite) _stream.Flush();
    _disposed = true;
    if (!_leaveOpen) _stream.Dispose();
  }

  private long OffsetTableOffset(int index) => HeaderSize + index * 4L;
  private long SpeedTableOffset(int index) => HeaderSize + _trackCount * 4L + index * 4L;

  private void ValidateIndex(int index) {
    if (index < 0 || index >= _trackCount)
      throw new ArgumentOutOfRangeException(nameof(index),
        "Mounted G64 writes cannot grow the offset/speed tables; use offline archive Add/Create for that relayout.");
  }

  private void EnsureWritable() {
    if (!CanWrite) throw new NotSupportedException("The G64 track device was opened read-only.");
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
