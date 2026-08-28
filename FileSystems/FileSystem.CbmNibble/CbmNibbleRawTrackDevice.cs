#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.CbmNibble;

/// <summary>
/// Opens the device layer below the pseudo-archive descriptors. A future
/// Commodore sector decoder can project these tracks as an
/// <see cref="IRandomAccessBlockDevice"/>, and a CBM DOS filesystem session can
/// then mount that block device without knowing whether the outer image was G64,
/// NIB, flux, or a physical drive.
/// </summary>
public static class CbmNibbleRawTrackDevices {
  public static IRawTrackDevice OpenG64(Stream image, bool writable, bool leaveOpen = true)
    => new G64Device(image, writable, leaveOpen);

  public static IRawTrackDevice OpenNib(Stream image, bool writable, bool leaveOpen = true)
    => new NibDevice(image, writable, leaveOpen);

  private sealed class G64Device : IRawTrackDevice {
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Dictionary<int, CbmNibbleReader.Track> _tracks;
    private readonly byte _version;
    private int _trackCount;
    private bool _dirty;
    private bool _disposed;

    public G64Device(Stream stream, bool writable, bool leaveOpen) {
      ArgumentNullException.ThrowIfNull(stream);
      if (!stream.CanRead || !stream.CanSeek)
        throw new ArgumentException("G64 track access requires a readable, seekable stream.", nameof(stream));
      if (writable && !stream.CanWrite)
        throw new ArgumentException("Writable G64 track access requires a writable stream.", nameof(stream));
      _stream = stream;
      _leaveOpen = leaveOpen;
      var parsed = CbmNibbleEntries.ReadImage(stream, "image.g64");
      if (writable) {
        var variable = parsed.Tracks.FirstOrDefault(t => t.SpeedZone > 3);
        if (variable != null)
          throw new NotSupportedException(
            $"G64 half-track {variable.Index} uses variable-speed map pointer 0x{variable.SpeedZone:X8}; writable raw-track access requires modeled speed-map blocks.");
      }
      CanWrite = writable;
      _version = parsed.Version;
      _trackCount = parsed.TrackCount;
      _tracks = parsed.Tracks.ToDictionary(t => t.Index,
        t => new CbmNibbleReader.Track(t.Index, t.Data.ToArray(), t.SpeedZone));
    }

    public int TrackCount => _trackCount;
    public bool CanWrite { get; }

    public IReadOnlyList<RawTrackInfo> EnumerateTracks() {
      ThrowIfDisposed();
      return Enumerable.Range(0, _trackCount)
        .Select(index => _tracks.TryGetValue(index, out var track)
          ? new RawTrackInfo(index, track.Data.LongLength, track.SpeedZone, track.Data.Length > 0)
          : new RawTrackInfo(index, 0, CbmNibbleEntries.DefaultSpeedZone(index), false))
        .ToArray();
    }

    public int ReadTrack(int index, Span<byte> destination) {
      ThrowIfDisposed();
      ValidateIndex(index);
      if (!_tracks.TryGetValue(index, out var track) || track.Data.Length == 0) return 0;
      if (destination.Length < track.Data.Length)
        throw new ArgumentException($"Destination is {destination.Length} bytes; track requires {track.Data.Length}.", nameof(destination));
      track.Data.CopyTo(destination);
      return track.Data.Length;
    }

    public void WriteTrack(int index, ReadOnlySpan<byte> source, uint? encodingParameter = null) {
      ThrowIfDisposed();
      EnsureWritable();
      if (index is < 0 or >= 84) throw new ArgumentOutOfRangeException(nameof(index));
      if (source.Length > ushort.MaxValue)
        throw new NotSupportedException("G64's track length field is 16-bit.");
      var speed = encodingParameter
        ?? (_tracks.TryGetValue(index, out var old) ? old.SpeedZone : CbmNibbleEntries.DefaultSpeedZone(index));
      if (speed > 3)
        throw new NotSupportedException("Pointer-based G64 variable-speed maps are not writable yet.");
      _tracks[index] = new CbmNibbleReader.Track(index, source.ToArray(), speed);
      _trackCount = Math.Max(_trackCount, index + 1);
      _dirty = true;
    }

    public void ClearTrack(int index) {
      ThrowIfDisposed();
      EnsureWritable();
      ValidateIndex(index);
      var speed = _tracks.TryGetValue(index, out var old)
        ? old.SpeedZone
        : CbmNibbleEntries.DefaultSpeedZone(index);
      _tracks[index] = new CbmNibbleReader.Track(index, [], speed);
      _dirty = true;
    }

    public void Flush() {
      ThrowIfDisposed();
      if (!CanWrite || !_dirty) {
        _stream.Flush();
        return;
      }
      var ordered = _tracks.Values.OrderBy(t => t.Index).ToArray();
      var rebuilt = CbmNibbleWriter.BuildG64FromTracks(ordered, _version, _trackCount);
      var verify = CbmNibbleReader.Read(rebuilt, "image.g64").Tracks.ToDictionary(t => t.Index);
      foreach (var expected in ordered.Where(t => t.Data.Length > 0))
        if (!verify.TryGetValue(expected.Index, out var actual) || !actual.Data.AsSpan().SequenceEqual(expected.Data))
          throw new InvalidOperationException($"G64 track-device commit changed track {expected.Index}.");
      CbmNibbleEntries.Commit(_stream, rebuilt);
      _dirty = false;
    }

    public void Dispose() {
      if (_disposed) return;
      if (CanWrite && _dirty) Flush();
      _disposed = true;
      if (!_leaveOpen) _stream.Dispose();
    }

    private void ValidateIndex(int index) {
      if (index < 0 || index >= _trackCount) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private void EnsureWritable() {
      if (!CanWrite) throw new NotSupportedException("The G64 raw-track device was opened read-only.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
  }

  private sealed class NibDevice : IRawTrackDevice {
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _disposed;

    public NibDevice(Stream stream, bool writable, bool leaveOpen) {
      ArgumentNullException.ThrowIfNull(stream);
      if (!stream.CanRead || !stream.CanSeek)
        throw new ArgumentException("NIB track access requires a readable, seekable stream.", nameof(stream));
      if (writable && !stream.CanWrite)
        throw new ArgumentException("Writable NIB track access requires a writable stream.", nameof(stream));
      _ = CbmNibbleEntries.ReadImage(stream, "image.nib");
      _stream = stream;
      _leaveOpen = leaveOpen;
      CanWrite = writable;
    }

    public int TrackCount => CbmNibbleReader.NibTrackCount;
    public bool CanWrite { get; }

    public IReadOnlyList<RawTrackInfo> EnumerateTracks() {
      ThrowIfDisposed();
      var result = new RawTrackInfo[TrackCount];
      var buffer = new byte[CbmNibbleReader.NibTrackSize];
      for (var index = 0; index < TrackCount; ++index) {
        var read = ReadSlot(index, buffer);
        var present = read == buffer.Length && buffer.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        result[index] = new RawTrackInfo(index, present ? buffer.Length : 0, 0, present);
      }
      return result;
    }

    public int ReadTrack(int index, Span<byte> destination) {
      ThrowIfDisposed();
      ValidateIndex(index);
      if (destination.Length < CbmNibbleReader.NibTrackSize)
        throw new ArgumentException($"NIB track reads require a {CbmNibbleReader.NibTrackSize}-byte destination.", nameof(destination));
      var read = ReadSlot(index, destination[..CbmNibbleReader.NibTrackSize]);
      if (read != CbmNibbleReader.NibTrackSize) return 0;
      return destination[..CbmNibbleReader.NibTrackSize].IndexOfAnyExcept((byte)0) < 0
        ? 0
        : CbmNibbleReader.NibTrackSize;
    }

    public void WriteTrack(int index, ReadOnlySpan<byte> source, uint? encodingParameter = null) {
      ThrowIfDisposed();
      EnsureWritable();
      ValidateIndex(index);
      if (source.Length != CbmNibbleReader.NibTrackSize)
        throw new NotSupportedException($"NIB tracks are fixed {CbmNibbleReader.NibTrackSize}-byte slots.");
      if (encodingParameter is > 0)
        throw new NotSupportedException("Raw NIB has no separate speed-zone field.");
      EnsureCanonicalLength();
      _stream.Position = index * (long)CbmNibbleReader.NibTrackSize;
      _stream.Write(source);
    }

    public void ClearTrack(int index) {
      ThrowIfDisposed();
      EnsureWritable();
      ValidateIndex(index);
      EnsureCanonicalLength();
      _stream.Position = index * (long)CbmNibbleReader.NibTrackSize;
      var zeros = new byte[CbmNibbleReader.NibTrackSize];
      _stream.Write(zeros);
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

    private int ReadSlot(int index, Span<byte> destination) {
      destination.Clear();
      _stream.Position = index * (long)CbmNibbleReader.NibTrackSize;
      var total = 0;
      while (total < destination.Length) {
        var read = _stream.Read(destination[total..]);
        if (read == 0) break;
        total += read;
      }
      return total;
    }

    private void EnsureCanonicalLength() {
      if (_stream.Length < CbmNibbleReader.NibExpectedFileSize)
        _stream.SetLength(CbmNibbleReader.NibExpectedFileSize);
    }

    private static void ValidateIndex(int index) {
      if (index is < 0 or >= CbmNibbleReader.NibTrackCount) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private void EnsureWritable() {
      if (!CanWrite) throw new NotSupportedException("The NIB raw-track device was opened read-only.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
  }
}
