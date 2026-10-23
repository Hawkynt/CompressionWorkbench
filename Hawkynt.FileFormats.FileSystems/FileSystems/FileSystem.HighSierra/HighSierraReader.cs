using System.Text;
using static FileSystem.HighSierra.HighSierraLayout;

namespace FileSystem.HighSierra;

/// <summary>One file or directory on a High Sierra volume.</summary>
/// <param name="Path">Slash-separated path with the version suffix (<c>;1</c>) and an empty extension's FULL STOP removed.</param>
/// <param name="IsDirectory">True for a directory.</param>
/// <param name="Size">Bytes of file data (sum over every extent of a multi-extent file).</param>
/// <param name="LastModified">Recording date of the (first) Directory Record; null when unspecified.</param>
/// <param name="Extents">The file's extents in order.</param>
/// <param name="IsHidden">The Existence flag: the file need not be shown to a user.</param>
public sealed record HighSierraEntry(string Path, bool IsDirectory, long Size, DateTime? LastModified,
  IReadOnlyList<HighSierraExtent> Extents, bool IsHidden);

/// <summary>One extent as a Directory Record describes it.</summary>
/// <param name="Lbn">Logical block number of the extent's first block.</param>
/// <param name="XarBlocks">Logical blocks of Extended Attribute Record before the data.</param>
/// <param name="Length">Data bytes in the extent.</param>
/// <param name="InterleaveSize">Consecutive blocks per file unit; 0 when not interleaved.</param>
/// <param name="InterleaveSkip">Blocks of other files between units.</param>
public readonly record struct HighSierraExtent(uint Lbn, byte XarBlocks, uint Length, byte InterleaveSize, byte InterleaveSkip);

/// <summary>
/// Reads a High Sierra (pre-ISO 9660, "CDROM" signature) volume: the Standard File
/// Structure Volume Descriptor and the directory hierarchy it roots.
/// </summary>
public sealed class HighSierraReader {
  /// <summary>Directory nesting accepted while walking (the paper allows eight levels; real discs are tolerated deeper).</summary>
  private const int MaxDepth = 64;

  private readonly Stream _stream;

  /// <summary>Logical block size from the volume descriptor (11.4.13).</summary>
  public int LogicalBlockSize { get; }

  /// <summary>Volume Identifier (11.4.7).</summary>
  public string VolumeId { get; }

  /// <summary>System Identifier (11.4.6).</summary>
  public string SystemId { get; }

  /// <summary>Volume Space Size in logical blocks (11.4.9).</summary>
  public uint VolumeSpaceSize { get; }

  /// <summary>Every file and directory, parents before children.</summary>
  public IReadOnlyList<HighSierraEntry> Entries { get; }

  /// <summary>Directories whose records could not all be read; their listing may be incomplete.</summary>
  public IReadOnlyList<string> Problems => this._problems;
  private readonly List<string> _problems = [];

  /// <summary>Opens <paramref name="stream"/>; throws <see cref="InvalidDataException"/> when no High Sierra descriptor is found.</summary>
  public HighSierraReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var pvd = FindStandardDescriptor(stream)
      ?? throw new InvalidDataException("Not a High Sierra volume: no 'CDROM' Standard File Structure Volume Descriptor from sector 16.");

    this.LogicalBlockSize = ReadBoth16(pvd.AsSpan(PvdLogicalBlockSize));
    if (this.LogicalBlockSize is not (512 or 1024 or 2048))
      throw new InvalidDataException($"High Sierra logical block size {this.LogicalBlockSize} is not 512, 1024 or 2048.");
    this.VolumeId = ReadPadded(pvd.AsSpan(PvdVolumeId, 32));
    this.SystemId = ReadPadded(pvd.AsSpan(PvdSystemId, 32));
    this.VolumeSpaceSize = ReadBoth32(pvd.AsSpan(PvdVolumeSpaceSize));

    var root = pvd.AsSpan(PvdRootRecord, 34);
    if (root[DrLength] < 34 || (root[DrFlags] & FlagDirectory) == 0)
      throw new InvalidDataException("High Sierra root Directory Record is malformed.");
    var entries = new List<HighSierraEntry>();
    this.Walk(ParseExtent(root), "", 0, entries, []);
    this.Entries = entries;
  }

  /// <summary>Returns the bytes of a file; refuses extents that leave the image.</summary>
  public byte[] Extract(HighSierraEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    using var ms = new MemoryStream(checked((int)entry.Size));
    foreach (var x in entry.Extents) this.CopyExtent(x, ms);
    return ms.ToArray();
  }

  private void CopyExtent(HighSierraExtent x, Stream output) {
    var lbs = this.LogicalBlockSize;
    long block = (long)x.Lbn + x.XarBlocks;
    long remaining = x.Length;
    // An interleaved extent records the file in units of InterleaveSize blocks with
    // InterleaveSkip blocks of other files between them (13.1.8, 13.1.9).
    var unit = x.InterleaveSize == 0 ? long.MaxValue : (long)x.InterleaveSize * lbs;
    var buffer = new byte[Math.Min(remaining, 64 * 1024)];
    while (remaining > 0) {
      var take = Math.Min(remaining, unit);
      var pos = block * lbs;
      if (pos + take > this._stream.Length)
        throw new InvalidDataException("High Sierra file extent extends past the end of the image.");
      this._stream.Position = pos;
      var left = take;
      while (left > 0) {
        var n = (int)Math.Min(left, buffer.Length);
        this._stream.ReadExactly(buffer, 0, n);
        output.Write(buffer, 0, n);
        left -= n;
      }
      remaining -= take;
      block += x.InterleaveSize + x.InterleaveSkip;
    }
  }

  private void Walk(HighSierraExtent dir, string prefix, int depth, List<HighSierraEntry> entries, HashSet<uint> visited) {
    if (depth > MaxDepth || !visited.Add(dir.Lbn)) {
      this._problems.Add($"{(prefix.Length == 0 ? "/" : prefix)}: directory loop or nesting beyond {MaxDepth}");
      return;
    }
    byte[] data;
    try {
      using var ms = new MemoryStream();
      this.CopyExtent(dir, ms);
      data = ms.ToArray();
    } catch (InvalidDataException) {
      this._problems.Add($"{(prefix.Length == 0 ? "/" : prefix)}: directory extent outside the image");
      return;
    }

    var children = new List<(string Name, HighSierraEntry Entry, HighSierraExtent Dir)>();
    HighSierraEntry? pending = null;
    var pendingExtents = new List<HighSierraExtent>();
    var off = 0;
    while (off < data.Length) {
      int len = data[off];
      // Records never cross a logical sector (8.3.1); a zero length pads to the next one.
      if (len == 0) {
        off = (off / SectorSize + 1) * SectorSize;
        continue;
      }
      if (len < 34 || off + len > data.Length) {
        this._problems.Add($"{(prefix.Length == 0 ? "/" : prefix)}: malformed Directory Record at offset {off}");
        break;
      }
      var rec = data.AsSpan(off, len);
      off += len;
      int nameLen = rec[DrNameLength];
      if (DrName + nameLen > len) continue;
      var raw = rec.Slice(DrName, nameLen);
      if (nameLen == 1 && raw[0] is 0 or 1) continue; // "." and ".."
      var flags = rec[DrFlags];
      if ((flags & FlagAssociated) != 0) continue;    // associated files are ignored in interchange (13.1.6)

      var extent = ParseExtent(rec);
      pendingExtents.Add(extent);
      if ((flags & FlagMultiExtent) != 0) continue;   // more records of this file follow

      var name = CleanName(Encoding.ASCII.GetString(raw));
      var path = prefix.Length == 0 ? name : prefix + "/" + name;
      var isDir = (flags & FlagDirectory) != 0;
      pending = new HighSierraEntry(path, isDir, isDir ? 0 : pendingExtents.Sum(e => (long)e.Length),
        DecodeRecordDate(rec.Slice(DrDate, 6)), pendingExtents.ToArray(), (flags & FlagHidden) != 0);
      children.Add((name, pending, extent));
      pendingExtents.Clear();
    }

    foreach (var (_, entry, extent) in children) {
      entries.Add(entry);
      if (entry.IsDirectory) this.Walk(extent, entry.Path, depth + 1, entries, visited);
    }
  }

  private static HighSierraExtent ParseExtent(ReadOnlySpan<byte> rec) => new(
    ReadBoth32(rec[DrExtent..]), rec[DrXarLength], ReadBoth32(rec[DrDataLength..]),
    rec[DrInterleaveSize], rec[DrInterleaveSkip]);

  /// <summary>Drops the File Version Number and the FULL STOP of an empty extension (10.5.2).</summary>
  private static string CleanName(string id) {
    var semi = id.LastIndexOf(';');
    if (semi >= 0) id = id[..semi];
    if (id.EndsWith('.')) id = id[..^1];
    return id.Length == 0 ? "_" : id;
  }

  private static byte[]? FindStandardDescriptor(Stream stream) {
    var buf = new byte[SectorSize];
    for (var sector = FirstDescriptorSector; sector < FirstDescriptorSector + 64; ++sector) {
      var pos = (long)sector * SectorSize;
      if (pos + SectorSize > stream.Length) return null;
      stream.Position = pos;
      stream.ReadExactly(buf);
      if (!buf.AsSpan(VdId, 5).SequenceEqual(StandardId)) return null;
      if (buf[VdType] == TypeStandard) return buf;
      if (buf[VdType] == TypeTerminator) return null;
    }
    return null;
  }
}
