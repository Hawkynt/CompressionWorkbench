namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// Read-only, seekable virtual guest-disk stream assembled from the member
/// devices of a <see cref="RaidArray"/>. It maps every virtual LBA to a
/// (member, byte-offset) pair according to the array's personality:
/// <list type="bullet">
///   <item><description><b>Linear</b> — member data regions concatenated in role order.</description></item>
///   <item><description><b>RAID0</b> — chunks round-robin across all members.</description></item>
///   <item><description><b>RAID1</b> — read from the first available mirror.</description></item>
///   <item><description><b>RAID4/5/6</b> — striped with parity chunks skipped; RAID5 uses
///     mdadm's default left-symmetric rotation, RAID6 left-symmetric P+Q.</description></item>
///   <item><description><b>RAID10</b> — stripe over mirrored pairs (near layout).</description></item>
/// </list>
/// A single missing member is transparently reconstructed by XOR where the level
/// permits (RAID5 always; RAID6 data-disk recovery from P; RAID1/10 from a
/// surviving mirror).
/// </summary>
public sealed class RaidAssembledStream : Stream {
  private readonly RaidArray _array;
  private readonly bool _leaveOpen;
  private readonly long _length;
  private long _position;

  /// <summary>The array this stream presents.</summary>
  public RaidArray Array => this._array;

  /// <summary>
  /// Builds a virtual stream over <paramref name="array"/>.
  /// </summary>
  /// <param name="array">The assembled array description.</param>
  /// <param name="leaveOpen">When <c>false</c>, member streams are disposed with this stream.</param>
  public RaidAssembledStream(RaidArray array, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(array);
    if (array.Members.Count != array.RaidDisks)
      throw new ArgumentException("Member count must equal RaidDisks (missing roles use placeholders).", nameof(array));

    var chunkRequired = array.Level is RaidLevel.Raid0 or RaidLevel.Raid4
      or RaidLevel.Raid5 or RaidLevel.Raid6 or RaidLevel.Raid10;
    if (chunkRequired && array.ChunkSizeBytes <= 0)
      throw new ArgumentException($"{array.Level} requires a positive chunk size.", nameof(array));

    this._array = array;
    this._leaveOpen = leaveOpen;
    this._length = ComputeLength(array);
  }

  private static long ComputeLength(RaidArray a) => a.Level switch {
    RaidLevel.Linear => a.Members.Sum(m => m.DataSizeBytes),
    RaidLevel.Raid1 => a.PerDeviceDataBytes,
    _ => (long)a.DataDisks * a.PerDeviceDataBytes,
  };

  /// <inheritdoc/>
    /// <summary>
  /// Gets a value indicating whether can read.
  /// </summary>
public override bool CanRead => true;
  /// <inheritdoc/>
    /// <summary>
  /// Gets a value indicating whether can seek.
  /// </summary>
public override bool CanSeek => true;
  /// <inheritdoc/>
    /// <summary>
  /// Gets a value indicating whether can write.
  /// </summary>
public override bool CanWrite => false;
  /// <inheritdoc/>
    /// <summary>
  /// Gets the length.
  /// </summary>
public override long Length => this._length;

  /// <inheritdoc/>
    /// <summary>
  /// Gets or sets the position.
  /// </summary>
public override long Position {
    get => this._position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  /// <inheritdoc/>
    /// <summary>
  /// Performs the seek operation.
  /// </summary>
public override long Seek(long offset, SeekOrigin origin) {
    this._position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    return this._position;
  }

  /// <inheritdoc/>
    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    return this.Read(buffer.AsSpan(offset, count));
  }

  /// <inheritdoc/>
    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public override int Read(Span<byte> buffer) {
    if (this._position >= this._length) return 0;
    var want = (int)Math.Min(buffer.Length, this._length - this._position);
    var done = 0;
    while (done < want) {
      var n = this.ReadRegion(buffer.Slice(done, want - done));
      if (n <= 0) break;
      this._position += n;
      done += n;
    }
    return done;
  }

  /// <summary>
  /// Fills as much of <paramref name="dest"/> as lies within the single contiguous
  /// on-member region containing the current position, and returns the byte count.
  /// </summary>
  private int ReadRegion(Span<byte> dest) => this._array.Level switch {
    RaidLevel.Linear => this.ReadLinear(dest),
    RaidLevel.Raid1 => this.ReadMirror(dest),
    RaidLevel.Raid0 => this.ReadStripe0(dest),
    RaidLevel.Raid4 or RaidLevel.Raid5 or RaidLevel.Raid6 => this.ReadStripeParity(dest),
    RaidLevel.Raid10 => this.ReadStripeMirror(dest),
    _ => throw new NotSupportedException($"Unsupported RAID level {this._array.Level}."),
  };

  // ── Linear ────────────────────────────────────────────────────────────
  private int ReadLinear(Span<byte> dest) {
    long start = 0;
    foreach (var m in this._array.Members) {
      var end = start + m.DataSizeBytes;
      if (this._position < end) {
        var rel = this._position - start;
        var n = (int)Math.Min(dest.Length, end - this._position);
        return ReadFromMember(m, rel, dest[..n]);
      }
      start = end;
    }
    return 0;
  }

  // ── RAID1 ─────────────────────────────────────────────────────────────
  private int ReadMirror(Span<byte> dest) {
    var m = this._array.Members.FirstOrDefault(x => x.IsPresent)
            ?? throw new InvalidOperationException("RAID1 array has no present mirror.");
    var n = (int)Math.Min(dest.Length, this._array.PerDeviceDataBytes - this._position);
    return ReadFromMember(m, this._position, dest[..n]);
  }

  // ── RAID0 ─────────────────────────────────────────────────────────────
  private int ReadStripe0(Span<byte> dest) {
    var chunk = this._array.ChunkSizeBytes;
    var vChunk = this._position / chunk;
    var inChunk = this._position % chunk;
    var n = (int)Math.Min(dest.Length, chunk - inChunk);

    var disk = (int)(vChunk % this._array.RaidDisks);
    var row = vChunk / this._array.RaidDisks;
    var rel = row * chunk + inChunk;

    var m = this._array.Members[disk];
    if (!m.IsPresent)
      throw new InvalidOperationException($"RAID0 member role {disk} is missing; no redundancy to reconstruct.");
    return ReadFromMember(m, rel, dest[..n]);
  }

  // ── RAID4/5/6 ─────────────────────────────────────────────────────────
  private int ReadStripeParity(Span<byte> dest) {
    var chunk = this._array.ChunkSizeBytes;
    var vChunk = this._position / chunk;
    var inChunk = this._position % chunk;
    var n = (int)Math.Min(dest.Length, chunk - inChunk);

    var dataDisks = this._array.DataDisks;
    var idxInStripe = (int)(vChunk % dataDisks);
    var stripe = vChunk / dataDisks;
    var rel = stripe * chunk + inChunk;

    var (disk, pd, qd) = this.MapParity(idxInStripe, stripe);

    var target = this._array.Members[disk];
    if (target.IsPresent)
      return ReadFromMember(target, rel, dest[..n]);

    // Reconstruct the missing chunk slice by XOR of the surviving members.
    this.ReconstructByXor(disk, pd, qd, rel, dest[..n]);
    return n;
  }

  /// <summary>
  /// Maps a logical data slot within a stripe to (physical data disk, parity disk,
  /// Q disk) for the array's parity level and layout. Q disk is -1 for RAID4/5.
  /// </summary>
  private (int disk, int pd, int qd) MapParity(int idxInStripe, long stripe) {
    var raidDisks = this._array.RaidDisks;
    var dataDisks = raidDisks - 1;

    switch (this._array.Level) {
      case RaidLevel.Raid4: {
        var pd = raidDisks - 1;
        return (idxInStripe, pd, -1);
      }
      case RaidLevel.Raid5: {
        var mod = (int)(stripe % raidDisks);
        switch (this._array.Layout) {
          case 0: { // left-asymmetric
            var pd = dataDisks - mod;
            var dd = idxInStripe >= pd ? idxInStripe + 1 : idxInStripe;
            return (dd, pd, -1);
          }
          case 1: { // right-asymmetric
            var pd = mod;
            var dd = idxInStripe >= pd ? idxInStripe + 1 : idxInStripe;
            return (dd, pd, -1);
          }
          case 3: { // right-symmetric
            var pd = mod;
            var dd = (pd + 1 + idxInStripe) % raidDisks;
            return (dd, pd, -1);
          }
          default: { // 2 = left-symmetric (mdadm default)
            var pd = dataDisks - mod;
            var dd = (pd + 1 + idxInStripe) % raidDisks;
            return (dd, pd, -1);
          }
        }
      }
      case RaidLevel.Raid6: {
        // Left-symmetric P+Q (mdadm default). Other RAID6 layouts are not supported.
        if (this._array.Layout is not (2 or 8))
          throw new NotSupportedException($"RAID6 layout {this._array.Layout} is not supported (only left-symmetric).");
        var mod = (int)(stripe % raidDisks);
        var pd = raidDisks - 1 - mod;
        var qd = (pd + 1) % raidDisks;
        var dd = (pd + 2 + idxInStripe) % raidDisks;
        return (dd, pd, qd);
      }
      default:
        throw new NotSupportedException($"Level {this._array.Level} is not a parity level.");
    }
  }

  /// <summary>
  /// Rebuilds the <paramref name="dest"/> slice of the missing member <paramref name="missing"/>
  /// at member-relative offset <paramref name="rel"/> by XOR-ing every surviving member that
  /// participates in the XOR parity (all others for RAID5; all but the Q disk for RAID6).
  /// </summary>
  private void ReconstructByXor(int missing, int pd, int qd, long rel, Span<byte> dest) {
    dest.Clear();
    var tmp = new byte[dest.Length];
    foreach (var m in this._array.Members) {
      if (m.Role == missing) continue;
      if (m.Role == qd) continue; // Q is Reed-Solomon, not part of the XOR sum.
      if (!m.IsPresent)
        throw new InvalidOperationException(
          $"Cannot reconstruct role {missing}: more than one member is missing (role {m.Role} also absent).");
      var read = ReadFromMember(m, rel, tmp.AsSpan(0, dest.Length));
      if (read != dest.Length)
        throw new EndOfStreamException($"Short read reconstructing role {missing} from role {m.Role}.");
      for (var i = 0; i < dest.Length; i++)
        dest[i] ^= tmp[i];
    }
    _ = pd; // pd participates implicitly (it is neither the missing nor the Q disk).
  }

  // ── RAID10 (near) ─────────────────────────────────────────────────────
  private int ReadStripeMirror(Span<byte> dest) {
    var chunk = this._array.ChunkSizeBytes;
    var near = Math.Max(1, this._array.NearCopies);
    var cols = this._array.RaidDisks / near;

    var vChunk = this._position / chunk;
    var inChunk = this._position % chunk;
    var n = (int)Math.Min(dest.Length, chunk - inChunk);

    var col = (int)(vChunk % cols);
    var row = vChunk / cols;
    var rel = row * chunk + inChunk;

    var first = col * near;
    for (var c = 0; c < near; c++) {
      var m = this._array.Members[first + c];
      if (m.IsPresent)
        return ReadFromMember(m, rel, dest[..n]);
    }
    throw new InvalidOperationException($"RAID10 column {col}: all {near} mirror copies are missing.");
  }

  // ── member IO ─────────────────────────────────────────────────────────
  private static int ReadFromMember(RaidMember m, long relOffset, Span<byte> dest) {
    if (m.Data is null)
      throw new InvalidOperationException($"Member role {m.Role} has no backing device.");
    m.Data.Position = m.DataOffsetBytes + relOffset;
    var total = 0;
    while (total < dest.Length) {
      var read = m.Data.Read(dest.Slice(total));
      if (read == 0) break;
      total += read;
    }
    // Past the physical end of a member reads as zeros (unwritten stripe tail).
    if (total < dest.Length)
      dest.Slice(total).Clear();
    return dest.Length;
  }

  /// <inheritdoc/>
    /// <summary>
  /// Performs the flush operation.
  /// </summary>
public override void Flush() { }
  /// <inheritdoc/>
    /// <summary>
  /// Sets the length.
  /// </summary>
public override void SetLength(long value) => throw new NotSupportedException();
  /// <inheritdoc/>
    /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("RAID assembly is read-only.");

  /// <inheritdoc/>
    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
protected override void Dispose(bool disposing) {
    if (disposing && !this._leaveOpen)
      foreach (var m in this._array.Members)
        m.Data?.Dispose();
    base.Dispose(disposing);
  }
}
