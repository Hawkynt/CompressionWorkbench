using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// Block cloning in the editor: a freed block that the block reference table says is cloned
/// gives up a reference instead of its space (<c>docs/ZFS-ON-DISK.md</c>, "Block cloning").
/// </summary>
internal static partial class ZfsPoolEditor {
  private const string BrtVdevPrefix = "com.fudosecurity:brt:vdev:";
  private const string BlockCloningFeature = "com.fudosecurity:block_cloning";
  private const int BrtHeaderWords = 7;

  /// <summary>One top-level vdev's block reference table as the edit leaves it.</summary>
  private sealed class BrtVdev {
    public ulong Object;
    /// <summary>brt_vdev_phys_t: entries ZAP, size, byte order, total count, range size, used and saved space.</summary>
    public required ulong[] Header;
    public required ushort[] Counts;
    public required Dictionary<ulong, ulong> Refcounts;
    public bool Dirty;
  }

  /// <summary>A BRT entry's value: the reference count as the eight bytes of a little-endian <c>u64</c>, stored as 1-byte integers.</summary>
  private static ulong RefcountOf(ZfsZapEntry e) {
    if (e.IntegerLength != 1 || e.Values.Length != 8) throw new InvalidDataException("ZFS: a BRT entry's count is not 8 bytes.");
    ulong value = 0;
    for (var i = 7; i >= 0; --i) value = value << 8 | e.Values[i];
    return value;
  }

  private static ulong[] RefcountBytes(ulong value) => Enumerable.Range(0, 8).Select(i => value >> (8 * i) & 0xFF).ToArray();

  private sealed partial class Plan {
    private readonly Dictionary<int, BrtVdev?> _brt = [];

    private BrtVdev? Brt(int vdev) {
      if (this._brt.TryGetValue(vdev, out var known)) return known;
      if (!this._s.IsActive(BlockCloningFeature)) return this._brt[vdev] = null;
      var old = this._mos.Old;
      var obj = old.Zap(1, "MOS object directory").FirstOrDefault(e => e.Name == BrtVdevPrefix + vdev)?.Value ?? 0;
      if (obj == 0) return this._brt[vdev] = null;
      var dnode = old.RequireDnode(obj, "BRT vdev");
      if (dnode.Bonus.Length < BrtHeaderWords * 8) throw new InvalidDataException("ZFS: a BRT vdev without its header.");
      var header = new ulong[BrtHeaderWords];
      for (var i = 0; i < header.Length; ++i) header[i] = BinaryPrimitives.ReadUInt64LittleEndian(dnode.Bonus.AsSpan(i * 8));
      if (header[2] != 0) throw new NotSupportedException("ZFS: a big-endian block reference table is not edited.");
      var counts = new ushort[header[1]];
      var blockSize = dnode.DataBlockSize;
      for (var i = 0; i < counts.Length; ++i) {
        var at = (long)i * 2;
        var block = old.ReadBlock(dnode, (ulong)(at / blockSize));
        counts[i] = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan((int)(at % blockSize)));
      }
      var refcounts = old.Zap(header[0], "BRT entries").ToDictionary(
        e => e.Key is [var offset] ? offset : throw new InvalidDataException("ZFS: a BRT entry not keyed by one offset."), RefcountOf);
      return this._brt[vdev] = new BrtVdev { Object = obj, Header = header, Counts = counts, Refcounts = refcounts };
    }

    /// <summary>
    /// Whether the block reference table keeps a freed block: an entry for its first DVA's offset
    /// with references left loses one (its space stays); without one the block is freed as usual.
    /// </summary>
    private bool BrtKeeps(ZfsBlockPointer bp) {
      var dva = bp.Dvas[0];
      if (dva.IsEmpty || this.Brt((int)dva.Vdev) is not { } brt) return false;
      var offset = (ulong)dva.Offset;
      var range = offset / brt.Header[4];
      if (range >= (ulong)brt.Counts.Length || brt.Counts[range] == 0) return false;
      if (!brt.Refcounts.TryGetValue(offset, out var refcount) || refcount == 0) return false;
      var dsize = (ulong)this.ChargeOf(bp).Used;
      brt.Refcounts[offset] = --refcount;
      brt.Header[6] -= dsize;                                           // saved space
      if (refcount == 0) {
        brt.Header[5] -= dsize;                                         // used space
        --brt.Header[3];                                                // total count
        --brt.Counts[range];
      }
      brt.Dirty = true;
      return true;
    }

    /// <summary>Writes every changed table back: entries, counts and header — or, empty, removes it and its feature reference.</summary>
    private void PlanBrt() {
      var mos = this._mos;
      foreach (var (vdev, brt) in this._brt) {
        if (brt is not { Dirty: true }) continue;
        if (brt.Header[3] == 0) {
          this.FreeObject(mos, brt.Header[0]);
          this.FreeObject(mos, brt.Object);
          this.RewriteZap(mos, 1, entries => entries.RemoveAll(e => e.Name == BrtVdevPrefix + vdev));
          this.AdjustFeature(BlockCloningFeature, -1);
          continue;
        }
        var entries = brt.Refcounts.Where(e => e.Value > 0)
          .Select(e => new ZfsZapEntry(e.Key.ToString("x"), 1, RefcountBytes(e.Value)) { Key = [e.Key] }).ToList();
        this.EditZap(mos, brt.Header[0], list => {
          list.Clear();
          list.AddRange(entries);
        });
        var d = this.Load(mos, brt.Object);
        mos.Dirty.Add(brt.Object);
        for (var i = 0; i < BrtHeaderWords; ++i) W64(d.Bonus, i * 8, brt.Header[i]);
        var blockSize = d.DataBlockSize;
        var bytes = new byte[Align(brt.Counts.Length * 2L, blockSize)];
        for (var i = 0; i < brt.Counts.Length; ++i) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), brt.Counts[i]);
        this.FreeTree(mos, d, spill: false);
        this.WriteContents(mos, d, Enumerable.Range(0, bytes.Length / blockSize).Select(i => (byte[]?)bytes.AsSpan(i * blockSize, blockSize).ToArray()).ToList(), blockSize);
      }
    }
  }
}
