#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Iso;

/// <summary>
/// In-place ISO 9660 block mover. ISO files are single-extent contiguous
/// by spec (ECMA-119), so moving a file means patching the directory record's
/// extent LBA and data length fields. The PVD is also patched for volume
/// space size adjustments if needed.
/// <para>
/// Streaming: reads only the PVD + directory sectors via a
/// <see cref="SectorCache"/>. A 100 GB DVD/BD image never gets loaded as a
/// whole — only the touched sectors are read, and each metadata write is
/// followed by a <see cref="Stream.Flush"/> barrier so a crash mid-move
/// can never reference garbage.
/// </para>
/// </summary>
public sealed class IsoBlockMover : IFilesystemBlockMover {
  private const int SectorSize = 2048;
  private const int PvdLba = 16;

  /// <summary>Byte offset of the first data sector (sector 0).</summary>
  public long FirstDataByte => 0;

  /// <summary>A sector. A directory record names one, not a byte.</summary>
  public int BlockSize => SectorSize;

  /// <summary>Sector size.</summary>
  public int SectorSize_ => SectorSize;

  /// <summary>
  /// Each call repoints the record it is given and nothing else, so an owner
  /// in several runs — which this format cannot produce — would be several
  /// calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full image be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <summary>
  /// Where each file's directory record sits, keyed by the sector it names.
  /// </summary>
  /// <remarks>
  /// Built once. Finding the record by walking the directory from the root on
  /// every move costs the square of the move count, and on a half-megabyte
  /// image the planning pass had not finished after ten minutes — which is why
  /// this mover was written and then not used.
  /// </remarks>
  private readonly Dictionary<int, long> _recordOfExtent = [];

  /// <summary>Reads the directory once and notes where every record is.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._recordOfExtent.Clear();

    using var cache = new SectorCache(image);
    var pvdOffset = (long)PvdLba * SectorSize;
    if (pvdOffset + SectorSize > image.Length) return;

    var pvd = new byte[SectorSize];
    cache.Read(pvdOffset, pvd);
    var rootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2));
    var rootLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10));

    foreach (var (extent, at) in Records(image, cache, rootLba, rootLength))
      this._recordOfExtent[extent] = at;
  }

  /// <summary>Every directory record, as the extent it names and where it sits.</summary>
  private static IEnumerable<(int Extent, long At)> Records(Stream image, SectorCache cache,
      int rootLba, int rootLength) {
    var rootOffset = (long)rootLba * SectorSize;
    var end = Math.Min(rootOffset + rootLength, image.Length);
    var sector = new byte[SectorSize];

    for (var sectorOffset = rootOffset; sectorOffset < end; sectorOffset += SectorSize) {
      var inSector = (int)Math.Min(SectorSize, end - sectorOffset);
      cache.Read(sectorOffset, sector.AsSpan(0, inSector));

      var pos = 0;
      while (pos < inSector) {
        var recordLength = sector[pos];
        if (recordLength == 0) break;                        // padding to the sector end
        if (recordLength < 33 || pos + recordLength > inSector) break;

        var extent = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(pos + 2));
        var nameLength = sector[pos + 32];
        if (nameLength > 0 && nameLength <= recordLength - 33) {
          var first = sector[pos + 33];
          // "." and ".." name the directory itself and its parent.
          if (!(nameLength == 1 && (first == 0 || first == 1)))
            yield return (extent, sectorOffset + pos);
        }
        pos += recordLength;
      }
    }
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Power-fail-safe in-place metadata update via targeted sector writes:
  /// reads the PVD sector through the cache, locates the root directory,
  /// reads the directory sector containing the matching record, patches the
  /// 8-byte extent-LBA field (LE + BE copies), writes the sector back, and
  /// flushes. No full-image load — multi-GB DVD/BD images require only a
  /// handful of sector reads/writes per move.
  /// </remarks>
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._recordOfExtent.Count == 0) this.Init(image);

    var oldLba = (int)(oldOffset / SectorSize);
    var newLba = (int)(newOffset / SectorSize);
    if (oldLba == newLba) return;

    if (!this._recordOfExtent.Remove(oldLba, out var recordOffset))
      throw new InvalidOperationException(
        $"ISO 9660: no directory record names sector {oldLba}, so '{fileName}' cannot be repointed.");

    // The extent is recorded twice, once each way round, as the standard asks.
    Span<byte> patch = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(patch, (uint)newLba);
    BinaryPrimitives.WriteUInt32BigEndian(patch[4..], (uint)newLba);
    image.Position = recordOffset + 2;
    image.Write(patch);
    image.Flush();

    this._recordOfExtent[newLba] = recordOffset;
  }

  /// <summary>
  /// Walks the root directory one sector at a time, locates the entry whose
  /// extent LBA matches <paramref name="oldLba"/> and whose name matches
  /// <paramref name="fileName"/>, then writes 8 patched bytes (LE+BE copies
  /// of the new LBA) directly to the stream at the entry's absolute offset.
  /// </summary>
  private static void PatchRootDirStream(Stream image, SectorCache cache, int rootLba, int rootLen,
      string fileName, int oldLba, int newLba) {
    var nameUpper = StripVersion(fileName).ToUpperInvariant();
    var rootOff = (long)rootLba * SectorSize;
    var endOff = rootOff + rootLen;
    if (endOff > image.Length) endOff = image.Length;

    // Iterate sector-by-sector (records never cross sector boundaries in ISO).
    var sector = ArrayPool<byte>.Shared.Rent(SectorSize);
    try {
      for (var sectorOff = rootOff; sectorOff < endOff; sectorOff += SectorSize) {
        var bytesInSector = (int)Math.Min(SectorSize, endOff - sectorOff);
        cache.Read(sectorOff, sector.AsSpan(0, bytesInSector));

        var pos = 0;
        while (pos < bytesInSector) {
          var recLen = sector[pos];
          if (recLen == 0) break; // padding to end of sector
          if (pos + recLen > bytesInSector) break;
          if (recLen < 33) break;

          var extLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(pos + 2));
          var nameLen = sector[pos + 32];

          if (nameLen > 0 && nameLen <= recLen - 33) {
            var first = sector[pos + 33];
            if (!(nameLen == 1 && (first == 0 || first == 1))) {
              var raw = Encoding.ASCII.GetString(sector, pos + 33, nameLen);
              var canonical = StripVersion(raw);
              if (extLba == oldLba &&
                  (canonical.Equals(nameUpper, StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("*", StringComparison.Ordinal))) {
                // Patch the extent LBA (both LE and BE copies). Write 8 bytes
                // back at the entry's absolute offset.
                Span<byte> patch = stackalloc byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(patch, (uint)newLba);
                BinaryPrimitives.WriteUInt32BigEndian(patch.Slice(4), (uint)newLba);
                image.Position = sectorOff + pos + 2;
                image.Write(patch);
                // Invalidate the cached sector so subsequent reads see fresh bytes.
                cache.Invalidate(sectorOff, SectorSize);
                return;
              }
            }
          }
          pos += recLen;
        }
      }
    } finally {
      ArrayPool<byte>.Shared.Return(sector);
    }
  }

  private static string StripVersion(string s) {
    var semi = s.IndexOf(';');
    return semi >= 0 ? s[..semi] : s;
  }
}
