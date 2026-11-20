using System.Buffers.Binary;
using System.Numerics;

namespace FileSystem.Zfs;

/// <summary>
/// dRAID (<c>docs/ZFS-ON-DISK.md</c>, "dRAID"): redundancy groups of <c>data + parity</c> columns laid
/// across the children in 16 MiB rows, the child order of each slice permuted by a fixed map; a
/// block is always written as full stripes. Single parity recovers one missing data column.
/// </summary>
internal sealed class ZfsDraidVdev : ZfsVdev {
  /// <summary>Height of a row on every child (the largest block size).</summary>
  public const long RowHeight = 16L << 20;
  private const ulong Seed = 0xd7a1d5eed;

  private readonly ZfsVdev[] _children;
  private readonly byte[] _perms;

  public ZfsDraidVdev(ZfsVdev[] children, int data, int parity, int spares, int groups, int ashift) {
    if (parity is < 1 or > 3 || data < 1 || spares < 0 || groups < 1 || children.Length < data + parity + spares)
      throw new InvalidDataException($"ZFS: dRAID with {children.Length} children, {data} data, {parity} parity, {spares} spares is malformed.");
    this._children = children;
    this.Data = data;
    this.Parity = parity;
    this.Spares = spares;
    this.Groups = groups;
    this.Ashift = ashift;
    this._perms = Permutations(children.Length, out var count);
    this.PermutationCount = count;
    if (this.GroupWidth * (long)groups % this.Disks != 0)
      throw new InvalidDataException("ZFS: dRAID groups do not fill whole rows.");
  }

  public int Data { get; }
  public int Parity { get; }
  public int Spares { get; }
  public int Groups { get; }
  public int Ashift { get; }
  public int PermutationCount { get; }
  public int Children => this._children.Length;
  public int GroupWidth => this.Data + this.Parity;
  /// <summary>Children that hold groups (the rest is distributed spare space).</summary>
  public int Disks => this._children.Length - this.Spares;
  /// <summary>Logical bytes of one group.</summary>
  public long GroupSize => this.GroupWidth * RowHeight;

  /// <summary>
  /// The permutation rows of a child count: a Fisher-Yates shuffle of each row from the one
  /// before (the identity first), drawing from xoroshiro128++ seeded with <c>0xd7a1d5eed</c>
  /// and the map's seed; the Fletcher-4 sum of the result must match the map's checksum.
  /// </summary>
  internal static byte[] Permutations(int children, out int count) {
    var map = ZfsDraidMaps.Maps.FirstOrDefault(m => m.Children == children);
    if (map.Children == 0) throw new NotSupportedException($"ZFS: no dRAID map for {children} children.");
    count = map.Permutations;
    var perms = new byte[children * map.Permutations];
    var state0 = Seed;
    var state1 = map.Seed;
    var previous = Enumerable.Range(0, children).Select(i => (byte)i).ToArray();
    for (var i = 0; i < map.Permutations; ++i) {
      var row = perms.AsSpan(i * children, children);
      previous.CopyTo(row);
      for (var j = children - 1; j > 0; --j) {
        // xoroshiro128++
        var s0 = state0;
        var s1 = state1;
        var result = BitOperations.RotateLeft(s0 + s1, 17) + s0;
        s1 ^= s0;
        state0 = BitOperations.RotateLeft(s0, 49) ^ s1 ^ (s1 << 21);
        state1 = BitOperations.RotateLeft(s1, 28);
        var k = (int)(result % (ulong)(j + 1));
        (row[j], row[k]) = (row[k], row[j]);
      }
      previous = row.ToArray();
    }
    Span<byte> sum = stackalloc byte[32];
    ZfsChecksum.Fletcher4(perms, sum);
    if (BinaryPrimitives.ReadUInt64LittleEndian(sum) != map.Checksum)
      throw new InvalidDataException("ZFS: the dRAID permutation map does not match its checksum.");
    return perms;
  }

  /// <summary>Space a block of <paramref name="physicalSize"/> bytes takes: whole rows of the group width.</summary>
  public long AllocatedSize(long physicalSize) {
    var rows = ((physicalSize - 1) / ((long)this.Data << this.Ashift)) + 1;
    return rows * this.GroupWidth << this.Ashift;
  }

  /// <summary>One column of one row of a block: which child, where on it, how many bytes (parity first).</summary>
  internal readonly record struct Column(int Child, long Offset, int Size, bool IsData);

  /// <summary>
  /// The rows of a block at <paramref name="offset"/> (a block may end one group and start the
  /// next, making two rows), each with its group-width columns, parity first.
  /// </summary>
  internal List<Column[]> Map(long offset, int length) {
    var rows = new List<Column[]>();
    var sector = 1L << this.Ashift;
    long remaining = (length + sector - 1) / sector * sector;
    while (remaining > 0) {
      var group = offset / this.GroupSize;
      var groupEnd = (group + 1) * this.GroupSize;
      var size = remaining;
      if (offset + this.AllocatedSize(size) > groupEnd)
        size = (groupEnd - offset) / this.GroupWidth * this.Data;
      rows.Add(this.Row(offset, size));
      offset += this.AllocatedSize(size);
      remaining -= size;
    }
    return rows;
  }

  private Column[] Row(long offset, long size) {
    var ashift = this.Ashift;
    var rowSectors = RowHeight >> ashift;
    var group = offset / this.GroupSize;
    var groupStart = group * this.GroupWidth % this.Disks;
    var b = (offset >> ashift) % (rowSectors * this.GroupWidth);
    var perm = group / this.Groups;
    var row = perm * (this.GroupWidth * (long)this.Groups / this.Disks) + group % this.Groups * this.GroupWidth / this.Disks;
    var physical = (rowSectors * row + b / this.GroupWidth) << ashift;
    var wrap = groupStart + this.GroupWidth > this.Disks ? this.Disks - groupStart : this.GroupWidth;

    var psize = size >> ashift;
    var q = psize / this.Data;
    var r = psize - q * this.Data;
    var bc = r == 0 ? 0 : r + this.Parity;
    var permIndex = perm % ((long)this.PermutationCount * this.Children);
    var baseRow = (int)(permIndex / this.Children) * this.Children;
    var iter = permIndex % this.Children;

    var columns = new Column[this.GroupWidth];
    for (var i = 0; i < this.GroupWidth; ++i) {
      if (i == wrap) physical += RowHeight;
      var c = (groupStart + i) % this.Disks;
      var child = (int)((this._perms[baseRow + c] + iter) % this.Children);
      var sectors = q == 0 && i >= bc ? 0 : i < bc ? q + 1 : q;
      columns[i] = new(child, physical, (int)(sectors << ashift), i >= this.Parity);
    }
    return columns;
  }

  public override byte[] Read(long offset, int length) {
    var data = new byte[length];
    var at = 0;
    foreach (var columns in this.Map(offset, length)) {
      var pieces = new byte[columns.Length][];
      var missing = -1;
      for (var c = this.Parity; c < columns.Length; ++c) {
        if (columns[c].Size == 0) {
          pieces[c] = [];
          continue;
        }
        try {
          pieces[c] = this._children[columns[c].Child].Read(columns[c].Offset, columns[c].Size);
        } catch (Exception e) when (e is InvalidDataException or NotSupportedException or ZfsDeviceMissingException) {
          if (missing >= 0) throw;
          missing = c;
        }
      }
      if (missing >= 0) {
        // P is the XOR of the data columns, each zero-padded to the parity column's length.
        var p = this._children[columns[0].Child].Read(columns[0].Offset, columns[0].Size);
        for (var c = this.Parity; c < columns.Length; ++c) {
          if (c == missing) continue;
          for (var i = 0; i < pieces[c].Length; ++i) p[i] ^= pieces[c][i];
        }
        pieces[missing] = p.AsSpan(0, columns[missing].Size).ToArray();
      }
      for (var c = this.Parity; c < columns.Length && at < length; ++c) {
        var n = Math.Min(pieces[c].Length, length - at);
        pieces[c].AsSpan(0, n).CopyTo(data.AsSpan(at));
        at += n;
      }
    }
    return data;
  }

  public override string Describe() =>
    $"draid{this.Parity}:{this.Data}d:{this.Children}c:{this.Spares}s({string.Join(", ", this._children.Select(c => c.Describe()))})";
}
