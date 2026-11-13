using System.Buffers.Binary;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// What a pool records about its own space next to what its block trees actually hold: per
/// filesystem the dataset's referenced / compressed / uncompressed bytes and the user and group
/// accounting objects, and the <c>$MOS</c> directory's usage; each counted again by walking every
/// block. OpenZFS keeps both sides equal transaction group by transaction group, so an edit must
/// move them by the same amounts.
/// </summary>
internal static class ZfsAccountingProbe {
  internal readonly record struct Space(long Used, long Compressed, long Uncompressed) {
    public static Space operator -(Space a, Space b) => new(a.Used - b.Used, a.Compressed - b.Compressed, a.Uncompressed - b.Uncompressed);
  }

  internal sealed class Ledger {
    public Space Recorded;
    public Space Counted;
    public readonly Dictionary<string, long> UsersRecorded = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> UsersCounted = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> GroupsRecorded = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> GroupsCounted = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> ProjectsRecorded = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> ProjectsCounted = new(StringComparer.Ordinal);
  }

  private static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);

  /// <summary>Ledgers by filesystem path (<c>""</c> for the root dataset) and <c>$MOS</c>.</summary>
  public static Dictionary<string, Ledger> Probe(params byte[][] devices) {
    using var pool = new ZfsPool(devices.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList(), leaveOpen: false);
    var result = new Dictionary<string, Ledger>(StringComparer.Ordinal);
    var mos = pool.Mos;
    var directory = mos.Zap(1, "object directory").ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

    void Dataset(ulong dirObject, string path) {
      var dir = mos.RequireDnode(dirObject, "dir");
      var head = U64(dir.Bonus, 8);
      var children = U64(dir.Bonus, 32);
      if (head != 0) {
        var ds = mos.RequireDnode(head, "dataset");
        var ledger = new Ledger { Recorded = new((long)U64(ds.Bonus, 72), (long)U64(ds.Bonus, 80), (long)U64(ds.Bonus, 88)) };
        var bp = ZfsBlockPointer.Read(ds.Bonus.AsSpan(128));
        if (!bp.IsHole && !bp.IsEncrypted) {
          ledger.Counted = CountObjset(pool, bp, ledger);
          result[path] = ledger;
        }
      }
      if (children == 0) return;
      foreach (var child in mos.Zap(children, "children")) {
        if (child.Name.StartsWith('$')) {
          if (child.Name == "$MOS") {
            var mosDir = mos.RequireDnode(child.Value, "$MOS");
            result["$MOS"] = new Ledger {
              Recorded = new((long)U64(mosDir.Bonus, 40), (long)U64(mosDir.Bonus, 48), (long)U64(mosDir.Bonus, 56)),
              Counted = CountObjset(pool, pool.MosRoot, null),
            };
          }
          continue;
        }
        Dataset(child.Value, path.Length == 0 ? child.Name : path + "/" + child.Name);
      }
    }
    Dataset(directory["root_dataset"], "");
    return result;
  }

  /// <summary>What a 512-byte unit of allocation is charged: 512 on plain vdevs, less on RAID-Z (its deflate ratio).</summary>
  private static long DeflateRatio(ZfsPool pool) {
    var tree = pool.Config.List("vdev_tree")!;
    if (tree.String("type") != "raidz") return 512;
    return ZfsRaidzVdev.DeflateRatio(tree.Lists("children").Length, (int)tree.UInt64("nparity")!.Value, (int)tree.UInt64("ashift")!.Value);
  }

  private static Space CountObjset(ZfsPool pool, ZfsBlockPointer root, Ledger? ledger) {
    long used = 0, compressed = 0, uncompressed = 0;
    var ratio = DeflateRatio(pool);
    void Count(ZfsBlockPointer bp) {
      if (bp.IsHole) return;
      if (bp.IsEmbedded) {
        // an embedded block takes no space: data is charged its logical size only
        if (bp.Type is 19 or 23 or 25 or 26 or 49) uncompressed += bp.LogicalSize;
        return;
      }
      foreach (var dva in bp.Dvas)
        if (!dva.IsEmpty) used += dva.AllocatedSectors * ratio;
      compressed += bp.PhysicalSize;
      var metadata = bp.Level > 0 || bp.Type is not (19 or 23 or 25 or 26 or 49);
      uncompressed += metadata ? bp.PhysicalSize : bp.LogicalSize;
    }
    void Tree(ZfsBlockPointer bp) {
      if (bp.IsHole) return;
      Count(bp);
      if (bp.IsEmbedded || bp.Level == 0) return;
      var data = pool.ReadBlock(bp, cache: false);
      for (var at = 0; at + 128 <= data.Length; at += 128)
        Tree(ZfsBlockPointer.Read(data.AsSpan(at)));
    }
    void Object(ZfsDnode d) {
      foreach (var bp in d.BlockPointers) Tree(bp);
      if (d.Spill is { } spill) Tree(spill);
    }

    Count(root);
    var block = pool.ReadBlock(root, cache: false);
    var meta = ZfsDnode.Read(block)!;
    Object(meta);
    var os = pool.ReadObjectSet(root);
    var perBlock = meta.DataBlockSize / 512;
    for (ulong id = 0; id <= meta.MaxBlockId; ++id) {
      var dnodes = os.ReadBlock(meta, id);
      for (var i = 0; i < perBlock;) {
        var at = i * 512;
        if (dnodes[at] == 0) {
          ++i;
          continue;
        }
        var d = ZfsDnode.Read(dnodes.AsSpan(at))!;
        Object(d);
        if (ledger != null && d.BonusType is 17 or 44)
          Charge(ledger, d, projects: block.Length >= 0x800 + 512 && block[0x800] != 0);
        i += 1 + dnodes[at + 0x0C];
      }
    }
    if (block.Length >= 2048) {
      foreach (var (at, into) in new[] { (0x400, ledger?.UsersRecorded), (0x600, ledger?.GroupsRecorded), (0x800, ledger?.ProjectsRecorded) }) {
        if (block.Length < at + 512 || block[at] == 0) continue;
        var special = ZfsDnode.Read(block.AsSpan(at))!;
        Object(special);
        if (into != null)
          foreach (var e in os.Zap(special)) into[e.Name] = (long)e.Value;
      }
    }
    return new(used, compressed, uncompressed);
  }

  /// <summary>An object's charge: 512 + its used bytes when user-accounted (flag 2), one object when object-accounted (flag 8).</summary>
  private static void Charge(Ledger ledger, ZfsDnode d, bool projects) {
    ulong uid, gid, project = 0;
    if (d.BonusType == 17) {
      uid = U64(d.Bonus, 128);
      gid = U64(d.Bonus, 136);
    } else {
      var header = ((BinaryPrimitives.ReadUInt16LittleEndian(d.Bonus.AsSpan(4)) >> 10) & 0x3F) * 8;
      uid = U64(d.Bonus, header + 24);
      gid = U64(d.Bonus, header + 32);
      if ((U64(d.Bonus, header + 48) & 0x0000800000000000UL) != 0) project = U64(d.Bonus, header + 128);
    }
    void Add(Dictionary<string, long> into, string key, long amount) => into[key] = into.GetValueOrDefault(key) + amount;
    if ((d.Flags & 2) != 0) {
      var amount = 512 + (long)d.UsedBytes;
      Add(ledger.UsersCounted, uid.ToString("x"), amount);
      Add(ledger.GroupsCounted, gid.ToString("x"), amount);
      if (projects) Add(ledger.ProjectsCounted, project.ToString("x"), amount);
    }
    if ((d.Flags & 8) != 0) {
      Add(ledger.UsersCounted, "obj-" + uid.ToString("x"), 1);
      Add(ledger.GroupsCounted, "obj-" + gid.ToString("x"), 1);
      if (projects) Add(ledger.ProjectsCounted, "obj-" + project.ToString("x"), 1);
    }
  }

  /// <summary>Every ledger moved its records by exactly what its trees moved by.</summary>
  public static void AssertMovedTogether(Dictionary<string, Ledger> before, Dictionary<string, Ledger> after) {
    Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
    Assert.Multiple(() => {
      foreach (var (key, a) in after) {
        var b = before[key];
        Assert.That(a.Recorded - b.Recorded, Is.EqualTo(a.Counted - b.Counted), $"{key}: space");
        foreach (var (recorded, counted, what) in new[] {
                   (Diff(a.UsersRecorded, b.UsersRecorded), Diff(a.UsersCounted, b.UsersCounted), "users"),
                   (Diff(a.GroupsRecorded, b.GroupsRecorded), Diff(a.GroupsCounted, b.GroupsCounted), "groups"),
                   (Diff(a.ProjectsRecorded, b.ProjectsRecorded), Diff(a.ProjectsCounted, b.ProjectsCounted), "projects") })
          Assert.That(recorded, Is.EquivalentTo(counted), $"{key}: {what}");
      }
    });
  }

  private static Dictionary<string, long> Diff(Dictionary<string, long> after, Dictionary<string, long> before) =>
    after.Keys.Union(before.Keys).Select(k => (k, after.GetValueOrDefault(k) - before.GetValueOrDefault(k)))
      .Where(p => p.Item2 != 0).ToDictionary(p => p.k, p => p.Item2, StringComparer.Ordinal);
}
