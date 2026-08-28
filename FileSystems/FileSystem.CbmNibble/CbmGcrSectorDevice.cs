#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.CbmNibble;

/// <summary>
/// Lossless 1541 sector projection over raw GCR tracks. Unlike the legacy
/// convenience decoder, this device fails closed: every standard sector must
/// be present exactly once with a valid header checksum, a valid data checksum,
/// a matching track number, and a consistent disk id before the image is
/// exposed as a block device.
/// </summary>
public sealed class CbmGcrSectorDevice : IRandomAccessBlockDevice {
  public const int SectorSize = 256;
  public const int SectorCount = 683;
  public const int DataLength = SectorSize * SectorCount;

  private const int TotalTracks = 35;
  private const int SyncMinimumBits = 10;
  private const int HeaderRawBytes = 8;
  private const int DataRawBytes = 260;
  private const int HeaderGap = 9;
  private const int TailGap = 8;
  private const int SyncBytes = 5;
  private const byte GapByte = 0x55;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  private static readonly byte[] NibbleToGcr = [
    0b01010, 0b01011, 0b10010, 0b10011,
    0b01110, 0b01111, 0b10110, 0b10111,
    0b01001, 0b11001, 0b11010, 0b11011,
    0b01101, 0b11101, 0b11110, 0b10101,
  ];

  private static readonly byte[] GcrToNibble = BuildDecodeTable();

  private readonly IRawTrackDevice _tracks;
  private readonly bool _ownsTracks;
  private readonly int? _fixedTrackLength;
  private readonly byte[] _data = new byte[DataLength];
  private readonly HashSet<int> _dirtyTracks = [];
  private readonly object _gate = new();
  private readonly byte _diskId1;
  private readonly byte _diskId2;
  private bool _disposed;

  /// <param name="fixedTrackLength">
  /// Null for variable-length G64 tracks; 8192 for fixed-slot NIB tracks.
  /// </param>
  public CbmGcrSectorDevice(
      IRawTrackDevice tracks,
      bool writable,
      int? fixedTrackLength = null,
      bool ownsTracks = true) {
    ArgumentNullException.ThrowIfNull(tracks);
    if (writable && !tracks.CanWrite)
      throw new ArgumentException("Writable GCR sector projection requires a writable raw-track device.", nameof(tracks));
    _tracks = tracks;
    _ownsTracks = ownsTracks;
    _fixedTrackLength = fixedTrackLength;
    CanWrite = writable;

    var infos = tracks.EnumerateTracks().ToDictionary(info => info.Index);
    byte? id1 = null;
    byte? id2 = null;

    for (var track = 1; track <= TotalTracks; ++track) {
      var rawIndex = (track - 1) * 2;
      if (!infos.TryGetValue(rawIndex, out var info) || !info.IsPresent || info.Length <= 0)
        throw new InvalidDataException($"GCR projection: required track {track} (half-track {rawIndex}) is missing.");
      if (info.EncodingParameter > 3)
        throw new NotSupportedException(
          $"GCR projection: half-track {rawIndex} uses a pointer-based variable-speed map; sector writes are not safe until that map is modeled.");
      if (info.Length > int.MaxValue)
        throw new NotSupportedException("GCR track is too large to decode in memory.");

      var raw = new byte[(int)info.Length];
      var read = tracks.ReadTrack(rawIndex, raw);
      if (read <= 0) throw new InvalidDataException($"GCR projection: track {track} is empty.");
      if (read != raw.Length) Array.Resize(ref raw, read);

      var sectors = DecodeTrack(raw, track, out var trackId1, out var trackId2);
      id1 ??= trackId1;
      id2 ??= trackId2;
      if (id1 != trackId1 || id2 != trackId2)
        throw new InvalidDataException($"GCR projection: track {track} uses a different disk id than previous tracks.");

      foreach (var pair in sectors)
        pair.Value.CopyTo(_data, SectorOffset(track, pair.Key));
    }

    if (writable) {
      // Half-tracks carrying independent data are legitimate for copy protection,
      // but a standard 1541 logical-sector rewrite cannot preserve their coupling
      // or timing semantics. Refuse writable projection rather than corrupt them.
      foreach (var info in infos.Values.Where(info => (info.Index & 1) != 0 && info.IsPresent && info.Length > 0)) {
        if (info.Length > int.MaxValue) throw new NotSupportedException("GCR half-track is too large to inspect.");
        var raw = new byte[(int)info.Length];
        var read = tracks.ReadTrack(info.Index, raw);
        if (read > 0 && !IsBlank(raw.AsSpan(0, read)))
          throw new NotSupportedException(
            $"GCR half-track {info.Index} contains non-gap data; writable sector projection would not preserve copy-protection/timing semantics.");
      }
    }

    _diskId1 = id1 ?? (byte)'0';
    _diskId2 = id2 ?? (byte)'0';
  }

  public BlockDeviceGeometry Geometry { get; } = new(SectorSize, SectorCount, SectorSize, false);
  public bool CanWrite { get; }

  public int ReadBlocks(long firstBlock, Span<byte> destination) {
    lock (_gate) {
      ThrowIfDisposed();
      var blocks = ValidateTransfer(firstBlock, destination.Length);
      if (blocks == 0) return 0;
      _data.AsSpan(checked((int)firstBlock * SectorSize), destination.Length).CopyTo(destination);
      return blocks;
    }
  }

  public void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source) {
    lock (_gate) {
      ThrowIfDisposed();
      EnsureWritable();
      var blocks = ValidateTransfer(firstBlock, source.Length);
      if (blocks == 0) return;
      source.CopyTo(_data.AsSpan(checked((int)firstBlock * SectorSize), source.Length));
      for (var block = (int)firstBlock; block < (int)firstBlock + blocks; ++block)
        _dirtyTracks.Add(TrackForBlock(block));
    }
  }

  public void Trim(long firstBlock, long blockCount) {
    ThrowIfDisposed();
    EnsureWritable();
    if (firstBlock < 0 || blockCount < 0 || firstBlock > SectorCount - blockCount)
      throw new ArgumentOutOfRangeException(nameof(firstBlock));
    throw new NotSupportedException("1541 sector media has no trim/discard primitive.");
  }

  public void Flush() {
    lock (_gate) {
      ThrowIfDisposed();
      if (!CanWrite || _dirtyTracks.Count == 0) {
        _tracks.Flush();
        return;
      }

      foreach (var track in _dirtyTracks.Order()) {
        var encoded = EncodeTrack(track);
        var rawIndex = (track - 1) * 2;
        if (_fixedTrackLength is int fixedLength) {
          if (encoded.Length > fixedLength)
            throw new IOException($"Canonical GCR track {track} is {encoded.Length} bytes and exceeds the {fixedLength}-byte media slot.");
          var slot = new byte[fixedLength];
          slot.AsSpan().Fill(GapByte);
          encoded.CopyTo(slot, 0);
          encoded = slot;
        }
        // Self-check before the raw media is touched.
        var verify = DecodeTrack(encoded, track, out var id1, out var id2);
        if (id1 != _diskId1 || id2 != _diskId2 || verify.Count != SectorsPerTrack[track])
          throw new InvalidOperationException($"GCR encoder self-check failed for track {track}.");
        _tracks.WriteTrack(rawIndex, encoded);
      }
      _tracks.Flush();
      _dirtyTracks.Clear();
    }
  }

  public void Dispose() {
    if (_disposed) return;
    if (CanWrite) Flush();
    _disposed = true;
    if (_ownsTracks) _tracks.Dispose();
  }

  private byte[] EncodeTrack(int track) {
    using var output = new MemoryStream();
    for (var sector = 0; sector < SectorsPerTrack[track]; ++sector) {
      Span<byte> header = stackalloc byte[HeaderRawBytes];
      header[0] = 0x08;
      header[2] = (byte)sector;
      header[3] = (byte)track;
      header[4] = _diskId2;
      header[5] = _diskId1;
      header[6] = 0x0F;
      header[7] = 0x0F;
      header[1] = (byte)(header[2] ^ header[3] ^ header[4] ^ header[5]);

      var dataBlock = new byte[DataRawBytes];
      dataBlock[0] = 0x07;
      _data.AsSpan(SectorOffset(track, sector), SectorSize).CopyTo(dataBlock.AsSpan(1, SectorSize));
      byte checksum = 0;
      for (var i = 0; i < SectorSize; ++i) checksum ^= dataBlock[i + 1];
      dataBlock[257] = checksum;

      WriteSync(output);
      output.Write(CbmGcr.Encode(header));
      WriteGap(output, HeaderGap);
      WriteSync(output);
      output.Write(CbmGcr.Encode(dataBlock));
      WriteGap(output, TailGap);
    }
    return output.ToArray();
  }

  private static Dictionary<int, byte[]> DecodeTrack(
      ReadOnlySpan<byte> raw,
      int expectedTrack,
      out byte diskId1,
      out byte diskId2) {
    var syncStarts = FindSyncEnds(raw);
    if (syncStarts.Count == 0)
      throw new InvalidDataException($"GCR projection: track {expectedTrack} contains no sync mark.");

    var result = new Dictionary<int, byte[]>();
    byte? id1 = null;
    byte? id2 = null;
    for (var i = 0; i < syncStarts.Count; ++i) {
      var headerBit = syncStarts[i];
      Span<byte> header = stackalloc byte[HeaderRawBytes];
      if (!TryDecodeBits(raw, headerBit, header) || header[0] != 0x08) continue;
      if (header[1] != (byte)(header[2] ^ header[3] ^ header[4] ^ header[5])) continue;
      if (header[3] != expectedTrack) continue;
      var sector = header[2];
      if (sector >= SectorsPerTrack[expectedTrack]) continue;

      var dataBit = syncStarts[(i + 1) % syncStarts.Count];
      Span<byte> data = stackalloc byte[DataRawBytes];
      if (!TryDecodeBits(raw, dataBit, data) || data[0] != 0x07) continue;
      byte checksum = 0;
      for (var p = 1; p <= SectorSize; ++p) checksum ^= data[p];
      if (checksum != data[257]) continue;
      if (result.ContainsKey(sector))
        throw new InvalidDataException($"GCR projection: track {expectedTrack} contains duplicate valid sector {sector}.");

      id1 ??= header[5];
      id2 ??= header[4];
      if (id1 != header[5] || id2 != header[4])
        throw new InvalidDataException($"GCR projection: track {expectedTrack} contains inconsistent disk ids.");
      result.Add(sector, data.Slice(1, SectorSize).ToArray());
    }

    if (result.Count != SectorsPerTrack[expectedTrack]) {
      var missing = Enumerable.Range(0, SectorsPerTrack[expectedTrack]).Where(s => !result.ContainsKey(s));
      throw new InvalidDataException(
        $"GCR projection: track {expectedTrack} decoded {result.Count}/{SectorsPerTrack[expectedTrack]} sectors; missing {string.Join(",", missing)}.");
    }
    diskId1 = id1 ?? throw new InvalidDataException($"GCR projection: track {expectedTrack} has no valid sector header.");
    diskId2 = id2 ?? throw new InvalidDataException($"GCR projection: track {expectedTrack} has no valid sector header.");
    return result;
  }

  /// <summary>
  /// Returns bit positions immediately after a sync run. The scan is circular,
  /// so a track rotated by a non-byte number of bits is still decoded.
  /// </summary>
  private static List<int> FindSyncEnds(ReadOnlySpan<byte> raw) {
    var totalBits = raw.Length * 8;
    var result = new List<int>();
    if (totalBits == 0) return result;

    var trailingOnes = 0;
    for (var bit = totalBits - 1; bit >= 0 && GetBit(raw, bit) != 0; --bit) trailingOnes++;
    var run = trailingOnes;
    for (var bit = 0; bit < totalBits; ++bit) {
      if (GetBit(raw, bit) != 0) {
        if (bit < trailingOnes && trailingOnes == totalBits) continue;
        run++;
        continue;
      }
      if (run >= SyncMinimumBits) result.Add(bit);
      run = 0;
    }
    return result.Distinct().Order().ToList();
  }

  private static bool TryDecodeBits(ReadOnlySpan<byte> raw, int startBit, Span<byte> destination) {
    var totalBits = raw.Length * 8;
    if (totalBits == 0 || destination.Length * 10 > totalBits) return false;
    var bit = startBit;
    for (var i = 0; i < destination.Length; ++i) {
      var hiCode = ReadBits(raw, ref bit, 5);
      var loCode = ReadBits(raw, ref bit, 5);
      var hi = GcrToNibble[hiCode];
      var lo = GcrToNibble[loCode];
      if (hi == 0xFF || lo == 0xFF) return false;
      destination[i] = (byte)((hi << 4) | lo);
    }
    return true;
  }

  private static int ReadBits(ReadOnlySpan<byte> raw, ref int bit, int count) {
    var totalBits = raw.Length * 8;
    var value = 0;
    for (var i = 0; i < count; ++i) {
      if (bit >= totalBits) bit = 0;
      value = (value << 1) | GetBit(raw, bit++);
    }
    return value;
  }

  private static int GetBit(ReadOnlySpan<byte> raw, int bit)
    => (raw[bit >> 3] >> (7 - (bit & 7))) & 1;

  private static byte[] BuildDecodeTable() {
    var table = new byte[32];
    Array.Fill(table, (byte)0xFF);
    for (var i = 0; i < NibbleToGcr.Length; ++i) table[NibbleToGcr[i]] = (byte)i;
    return table;
  }

  private static bool IsBlank(ReadOnlySpan<byte> raw) {
    foreach (var value in raw)
      if (value != 0x00 && value != GapByte) return false;
    return true;
  }

  private static int ValidateTransfer(long firstBlock, int byteCount) {
    if (byteCount < 0 || byteCount % SectorSize != 0)
      throw new ArgumentException($"Block transfers must be a multiple of {SectorSize} bytes.", nameof(byteCount));
    var blocks = byteCount / SectorSize;
    if (firstBlock < 0 || firstBlock > SectorCount - blocks)
      throw new ArgumentOutOfRangeException(nameof(firstBlock));
    return blocks;
  }

  private static int TrackForBlock(int block) {
    var first = 0;
    for (var track = 1; track <= TotalTracks; ++track) {
      var next = first + SectorsPerTrack[track];
      if (block < next) return track;
      first = next;
    }
    throw new ArgumentOutOfRangeException(nameof(block));
  }

  private static int SectorOffset(int track, int sector) {
    var offset = 0;
    for (var t = 1; t < track; ++t) offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
  }

  private static void WriteSync(Stream output) {
    for (var i = 0; i < SyncBytes; ++i) output.WriteByte(0xFF);
  }

  private static void WriteGap(Stream output, int count) {
    for (var i = 0; i < count; ++i) output.WriteByte(GapByte);
  }

  private void EnsureWritable() {
    if (!CanWrite) throw new NotSupportedException("The GCR sector device was opened read-only.");
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
