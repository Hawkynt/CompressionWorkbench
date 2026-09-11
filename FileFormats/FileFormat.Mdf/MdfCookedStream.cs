#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Mdf;

/// <summary>
/// Presents the 2 048-byte user-data field of every MDF sector as a contiguous
/// ISO-style stream while keeping the physical MDF sector count fixed.
/// Writes delegate to <see cref="MdfInPlaceModifier"/>, so raw-sector EDC/ECC is
/// regenerated instead of becoming stale.
/// </summary>
internal sealed class MdfCookedStream : Stream {
  private const int LogicalSectorSize = MdfInPlaceModifier.Iso9660SectorSize;

  private readonly Stream _physical;
  private readonly bool _leaveOpen;
  private long _position;

  public MdfInPlaceModifier.SectorGeometry Geometry { get; }
  public long SectorCount => this._physical.Length / this.Geometry.SectorSize;

  public MdfCookedStream(Stream physical, bool leaveOpen = false) {
    this._physical = physical ?? throw new ArgumentNullException(nameof(physical));
    if (!physical.CanRead || !physical.CanSeek)
      throw new ArgumentException("MDF logical access requires a readable, seekable stream.", nameof(physical));
    this.Geometry = MdfInPlaceModifier.DetectGeometry(physical);
    if (physical.Length % this.Geometry.SectorSize != 0)
      throw new InvalidDataException(
        $"MDF length {physical.Length} is not a multiple of the detected {this.Geometry.SectorSize}-byte sector size.");
    this._leaveOpen = leaveOpen;
  }

  public override bool CanRead => this._physical.CanRead;
  public override bool CanSeek => this._physical.CanSeek;
  public override bool CanWrite => this._physical.CanWrite;
  public override long Length => checked(this.SectorCount * LogicalSectorSize);

  public override long Position {
    get => this._position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  public override void Flush() => this._physical.Flush();

  public override int Read(byte[] buffer, int offset, int count)
    => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    if (this._position >= this.Length || buffer.IsEmpty) return 0;

    var remaining = (int)Math.Min(buffer.Length, this.Length - this._position);
    var written = 0;
    var sector = new byte[LogicalSectorSize];
    while (written < remaining) {
      var lba = this._position / LogicalSectorSize;
      var inSector = (int)(this._position % LogicalSectorSize);
      var chunk = Math.Min(LogicalSectorSize - inSector, remaining - written);
      MdfInPlaceModifier.ReadSector(this._physical, lba, sector, this.Geometry);
      sector.AsSpan(inSector, chunk).CopyTo(buffer[written..]);
      written += chunk;
      this._position += chunk;
    }
    return written;
  }

  public override void Write(byte[] buffer, int offset, int count)
    => this.Write(buffer.AsSpan(offset, count));

  public override void Write(ReadOnlySpan<byte> buffer) {
    if (!this.CanWrite)
      throw new NotSupportedException("The MDF stream is read-only.");
    if (buffer.IsEmpty) return;
    if (this._position > this.Length - buffer.Length)
      throw new IOException(
        "MDF/MDS has fixed physical track geometry in the single-stream API; writes may not grow the MDF beyond its existing sector count.");

    var consumed = 0;
    var sector = new byte[LogicalSectorSize];
    while (consumed < buffer.Length) {
      var lba = checked((int)(this._position / LogicalSectorSize));
      var inSector = (int)(this._position % LogicalSectorSize);
      var chunk = Math.Min(LogicalSectorSize - inSector, buffer.Length - consumed);

      if (inSector == 0 && chunk == LogicalSectorSize) {
        MdfInPlaceModifier.WriteSector(this._physical, lba, buffer.Slice(consumed, chunk), this.Geometry);
      } else {
        MdfInPlaceModifier.ReadSector(this._physical, lba, sector, this.Geometry);
        buffer.Slice(consumed, chunk).CopyTo(sector.AsSpan(inSector, chunk));
        MdfInPlaceModifier.WriteSector(this._physical, lba, sector, this.Geometry);
      }

      consumed += chunk;
      this._position += chunk;
    }
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var target = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => checked(this._position + offset),
      SeekOrigin.End => checked(this.Length + offset),
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (target < 0) throw new IOException("Cannot seek before the beginning of the MDF logical stream.");
    return this._position = target;
  }

  public override void SetLength(long value) {
    if (value != this.Length)
      throw new NotSupportedException(
        "Changing MDF physical length requires updating the companion MDS track descriptors, which the single-stream archive API cannot do safely.");
  }

  protected override void Dispose(bool disposing) {
    if (disposing && !this._leaveOpen)
      this._physical.Dispose();
    base.Dispose(disposing);
  }
}

/// <summary>ISO-9660 operations performed through the fixed-size MDF logical view.</summary>
internal static class MdfIsoOperations {
  private const int SectorSize = MdfInPlaceModifier.Iso9660SectorSize;
  private const int PvdLba = 16;

  public static void AddOrReplace(Stream physical, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(physical);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    EnsureRootName(name);

    MutateTransactionally(physical, cooked => {
      // Removing first is safe because this is the staged copy. It also releases
      // a trailing extent before capacity is checked, so replacing the last file
      // in a physically full MDF can reuse its own sectors instead of spuriously
      // demanding growth that would invalidate the companion MDS.
      FileSystem.Iso.IsoModifier.RemoveFile(cooked, Path.GetFileName(name), wipeData: true);
      TrimLogicalVolumeSpace(cooked);
      EnsureCapacityForAppend(cooked, data.Length);
      FileSystem.Iso.IsoModifier.AddFile(cooked, Path.GetFileName(name), data);
    });
  }

  public static void Remove(Stream physical, string name) {
    ArgumentNullException.ThrowIfNull(physical);
    ArgumentNullException.ThrowIfNull(name);
    EnsureRootName(name);

    MutateTransactionally(physical, cooked => {
      if (!FileSystem.Iso.IsoModifier.RemoveFile(cooked, Path.GetFileName(name), wipeData: true))
        return;
      TrimLogicalVolumeSpace(cooked);
    });
  }

  public static long WipeUnusedSpace(Stream physical, bool wipeClusterTips, bool wipeDeletedEntries) {
    ArgumentNullException.ThrowIfNull(physical);
    using var cooked = new MdfCookedStream(physical, leaveOpen: true);
    return new FileSystem.Iso.IsoFormatDescriptor()
      .WipeUnusedSpace(cooked, wipeClusterTips, wipeDeletedEntries);
  }

  public static void Purge(Stream physical) {
    ArgumentNullException.ThrowIfNull(physical);
    MutateTransactionally(physical, cooked => {
      var writer = new FileSystem.Iso.IsoWriter();
      var empty = writer.Build();
      if (empty.LongLength > cooked.Length)
        throw new IOException("The existing MDF track is too small to hold an empty ISO 9660 filesystem.");

      cooked.Position = 0;
      cooked.Write(empty);
      var zeros = new byte[64 * 1024];
      while (cooked.Position < cooked.Length) {
        var count = (int)Math.Min(zeros.Length, cooked.Length - cooked.Position);
        cooked.Write(zeros.AsSpan(0, count));
      }
    });
  }

  public static void Defragment(Stream physical, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(physical);
    ArgumentNullException.ThrowIfNull(options);
    MutateTransactionally(physical, cooked => {
      // ISO's native mover operates in 2 048-byte logical-sector coordinates;
      // MdfCookedStream maps every write back into the original MDF geometry.
      new FileSystem.Iso.IsoFormatDescriptor().Defragment(cooked, options);
      TrimLogicalVolumeSpace(cooked);
    });
  }

  private static void EnsureRootName(string name) {
    var normalized = name.Replace('\\', '/').Trim('/');
    if (normalized.Length == 0 || normalized.Contains('/'))
      throw new NotSupportedException(
        "MDF file-level editing currently supports the ISO 9660 root directory. Nested-path edits require a directory-growing ISO modifier.");
  }

  private static void EnsureCapacityForAppend(MdfCookedStream cooked, int dataLength) {
    Span<byte> pvd = stackalloc byte[SectorSize];
    cooked.Position = (long)PvdLba * SectorSize;
    cooked.ReadExactly(pvd);
    if (!pvd[1..6].SequenceEqual("CD001"u8))
      throw new InvalidDataException("MDF does not contain an ISO 9660 primary volume descriptor at LBA 16.");

    var volumeSpace = BinaryPrimitives.ReadUInt32LittleEndian(pvd[80..84]);
    var sectorsNeeded = Math.Max(1L, (dataLength + (long)SectorSize - 1) / SectorSize);
    if ((long)volumeSpace + sectorsNeeded > cooked.SectorCount)
      throw new IOException(
        $"MDF track has no fixed-capacity room for {sectorsNeeded} additional ISO sector(s); " +
        "growing it would require rewriting the companion MDS track geometry.");
  }

  /// <summary>
  /// Lowers ISO's logical volume-space count to the highest still-live extent.
  /// The physical MDF remains the same size, so the MDS track length stays valid;
  /// the released tail becomes reusable headroom for subsequent appends.
  /// </summary>
  private static void TrimLogicalVolumeSpace(MdfCookedStream cooked) {
    cooked.Position = 0;
    var extents = FileSystem.Iso.IsoExtentMap.Enumerate(cooked).ToList();
    if (extents.Count == 0) return;

    var highest = extents
      .Where(e => e.Length > 0)
      .Select(e => checked(e.Offset + e.Length))
      .DefaultIfEmpty((PvdLba + 2L) * SectorSize)
      .Max();
    var sectors = Math.Max(PvdLba + 2L, (highest + SectorSize - 1) / SectorSize);
    sectors = Math.Min(sectors, cooked.SectorCount);

    Span<byte> pvd = stackalloc byte[SectorSize];
    cooked.Position = (long)PvdLba * SectorSize;
    cooked.ReadExactly(pvd);
    if (!pvd[1..6].SequenceEqual("CD001"u8)) return;

    var current = BinaryPrimitives.ReadUInt32LittleEndian(pvd[80..84]);
    if (current != 0 && sectors >= current) return;
    BinaryPrimitives.WriteUInt32LittleEndian(pvd[80..84], checked((uint)sectors));
    BinaryPrimitives.WriteUInt32BigEndian(pvd[84..88], checked((uint)sectors));
    cooked.Position = (long)PvdLba * SectorSize;
    cooked.Write(pvd);
  }

  private static void MutateTransactionally(Stream physical, Action<MdfCookedStream> mutation) {
    if (!physical.CanRead || !physical.CanWrite || !physical.CanSeek)
      throw new ArgumentException("MDF modification requires a readable, writable, seekable stream.", nameof(physical));

    var originalPosition = physical.Position;
    using var staged = RebuildVerb.CreateScratchStream();
    physical.Position = 0;
    physical.CopyTo(staged);
    staged.Position = 0;

    using (var cooked = new MdfCookedStream(staged, leaveOpen: true)) {
      mutation(cooked);
      cooked.Flush();
    }

    staged.Position = 0;
    using (var verify = new MdfReader(staged, leaveOpen: true)) {
      _ = verify.Entries.Count;
    }

    if (staged.Length != physical.Length)
      throw new InvalidDataException("MDF mutation changed the physical sector count; companion MDS geometry would no longer match.");

    staged.Position = 0;
    physical.Position = 0;
    staged.CopyTo(physical);
    physical.SetLength(staged.Length);
    physical.Flush();
    physical.Position = Math.Min(originalPosition, physical.Length);
  }
}
