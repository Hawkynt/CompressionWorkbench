using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileSystem.Zfs;

/// <summary>
/// Snapshots in the editor: adding <c>&lt;dataset&gt;/.zfs/snapshot/&lt;name&gt;</c> as a directory creates
/// one, removing it destroys it (<c>docs/ZFS-ON-DISK.md</c>, "Creating and destroying snapshots").
/// </summary>
internal static partial class ZfsPoolEditor {
  private const byte OtDslDataset = 16, OtDeadlist = 50, OtDeadlistHeader = 51;
  private const ulong DsFlagUniqueAccurate = 4;
  private const string SnapshotCountField = "com.joyent:snapshot_count";
  private const string SnapshotsChangedField = "com.ixsystems:snapshots_changed";
  private const string BookmarksField = "com.delphix:bookmarks";
  private const ulong BookmarkHasFbn = 1, BookmarkSnapshotExists = 2;
  private const ulong DsFlagCaseInsensitive = 1UL << 16;

  /// <summary>A snapshot to create or destroy; an empty name asks for nothing (the <c>.zfs</c> directories themselves).</summary>
  private sealed record SnapshotOp(string Dataset, ulong Dir, ulong Head, string Name, bool Create);

  private sealed partial class Plan {
    private readonly List<SnapshotOp> _snapshotOps = [];
    private long _emptyBpobjDelta;
    private ulong _emptyBpobjNew;
    private readonly Dictionary<ulong, (Dnode Dnode, List<byte[]> Stored, long Used, long Compressed, long Uncompressed)> _bpobjAppends = [];

    // ── which paths name snapshots ─────────────────────────────────────

    /// <summary>The snapshot a path under <c>.zfs</c> names, or <see langword="null"/> for an ordinary path.</summary>
    private SnapshotOp? SnapshotTarget(string path, bool create) {
      foreach (var (dsPath, dir, head) in this._s.Datasets.OrderByDescending(d => d.Path.Length)) {
        string rel;
        if (dsPath.Length == 0) rel = path;
        else if (path == dsPath) rel = "";
        else if (path.StartsWith(dsPath + "/", StringComparison.Ordinal)) rel = path[(dsPath.Length + 1)..];
        else continue;
        var parts = rel.Split('/');
        if (parts[0] != ".zfs") return null;
        if (create && (parts.Length == 1 || (parts.Length == 2 && parts[1] == "snapshot")))
          return new(dsPath, dir, head, "", true);
        if (parts.Length == 3 && parts[1] == "snapshot") {
          CheckSnapshotName(dsPath, parts[2]);
          return new(dsPath, dir, head, parts[2], create);
        }
        throw new NotSupportedException("ZFS: snapshots are read-only; a snapshot is only created or destroyed whole, as .zfs/snapshot/<name>.");
      }
      return null;
    }

    private static void CheckSnapshotName(string dataset, string name) {
      // zfs_component_namecheck: letters, digits and "-_.: ", the whole name under 256 bytes
      if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or ' ')) || name is "." or "..")
        throw new ArgumentException($"ZFS: '{name}' is not a valid snapshot name.");
      if (dataset.Length + 1 + name.Length > 255)
        throw new ArgumentException($"ZFS: the snapshot name '{name}' is too long.");
    }

    /// <summary>Keeps the snapshot operations that change something; refuses mixing them with file changes in their dataset.</summary>
    private void AcceptSnapshotOps(List<SnapshotOp> ops) {
      if (ops.Count == 0) return;
      var edited = this._filesystems.Select(f => f.HeadObject).ToHashSet();
      foreach (var group in ops.GroupBy(o => o.Head)) {
        if (edited.Contains(group.Key))
          throw new NotSupportedException("ZFS: a snapshot is created or destroyed in an edit of its own, without file changes in its dataset.");
        if (group.Count() > 1)
          throw new NotSupportedException("ZFS: one snapshot per dataset is created or destroyed in an edit.");
      }
      foreach (var op in ops)
        if ((this.SnapshotObject(op.Head, op.Name) != 0) != op.Create)
          this._snapshotOps.Add(op);
    }

    private ulong SnapshotObject(ulong head, string name) {
      var names = U64(this._mos.Old.RequireDnode(head, "DSL dataset").Bonus, 32);
      return names == 0 ? 0 : this._mos.Old.Zap(names, "snapshot names").FirstOrDefault(e => e.Name == name)?.Value ?? 0;
    }

    // ── create ─────────────────────────────────────────────────────────

    /// <summary>
    /// A new snapshot object takes the head as it is (block pointer, sizes, deadlist); the head gets
    /// a deadlist of empty entries (the old keys and its previous snapshot's txg), a unique size of 0
    /// and the snapshot as its previous one.
    /// </summary>
    private void PlanCreateSnapshot(SnapshotOp op) {
      var mos = this._mos;
      var headNode = this.Load(mos, op.Head);
      mos.Dirty.Add(op.Head);
      var head = headNode.Bonus;
      var prevObj = U64(head, 8);
      var oldPrevTxg = U64(head, 16);
      var oldDeadlist = U64(head, 64);
      if (mos.Old.RequireDnode(oldDeadlist, "deadlist").Type != OtDeadlist)
        throw new NotSupportedException("ZFS: a dataset with an old-format deadlist gets no snapshot here.");
      var root = head.Slice(128, Bp).ToArray();
      if (IsHole(root)) throw new NotSupportedException($"ZFS: dataset '{op.Dataset}' has no object set.");
      if ((U64(head, 120) & DsFlagCaseInsensitive) != 0)
        throw new NotSupportedException("ZFS: case-insensitive datasets get no snapshots here.");
      var rootBp = ZfsBlockPointer.Read(root);
      if (rootBp.IsEncrypted) throw new NotSupportedException("ZFS: encrypted datasets get no snapshots here.");
      var objset = this._s.Pool.ReadBlock(rootBp, cache: false).ToArray();
      if (U64(objset, 0x200) != 0 || this.LogBlockWritten(objset.AsSpan(0x210, Bp).ToArray()))
        throw new NotSupportedException($"ZFS: dataset '{op.Dataset}' has an intent log that was never replayed.");

      // the snapshot carries the head's per-dataset features, each counted once more
      var features = this.DatasetFeatureEntries(op.Head);
      var snap = Dnode.Create(features.Count > 0 ? OtZapMetadata : OtDslDataset, OtDslDataset, 320, 512, 17, 1, FlagUsedBytes);
      var snapObj = this.NewObject(mos, snap);
      if (features.Count > 0) {
        var (blockSize, blocks) = ZfsWriter.BuildZap(features, fat: false);
        this.WriteContents(mos, snap, blocks, blockSize);
        foreach (var f in features) this.AdjustFeature(f.Name, +1);
      }
      var s = snap.Bonus;
      W64(s, 0, op.Dir);
      W64(s, 8, prevObj);
      W64(s, 16, oldPrevTxg);
      W64(s, 24, op.Head);
      W64(s, 40, 1);                                                // num_children
      W64(s, 48, (ulong)new DateTimeOffset(this._now).ToUnixTimeSeconds());
      W64(s, 56, this.Txg);
      W64(s, 64, oldDeadlist);
      head.Slice(72, 24).CopyTo(s[72..]);                           // referenced, compressed, uncompressed
      W64(s, 104, NewGuid() & ((1UL << 56) - 1));                   // fsid_guid
      W64(s, 112, NewGuid());
      W64(s, 120, U64(head, 120));
      root.CopyTo(s[128..]);

      // the previous snapshot leads to the new one (or, if the head is a clone of it, lists it as a clone)
      if (prevObj != 0) {
        var prev = this.Load(mos, prevObj);
        mos.Dirty.Add(prevObj);
        if (U64(prev.Bonus, 24) == op.Head) W64(prev.Bonus, 24, snapObj);
        else if (U64(prev.Bonus, 256) is var clones and not 0) this.ReplaceClone(clones, op.Head, snapObj);
      }

      // the head's new deadlist: every key it had and its previous snapshot's txg, all empty
      // (and the txg of every bookmark after it that counts frees: its range starts there)
      var keys = mos.Old.Zap(oldDeadlist, "deadlist").Select(e => Convert.ToUInt64(e.Name, 16)).Append(oldPrevTxg)
        .Concat(this.Bookmarks(op.Head).Entries.Where(b => CountsFrees(b) && BookmarkTxg(b) > oldPrevTxg).Select(BookmarkTxg))
        .Distinct().Order().ToList();
      var deadlist = Dnode.Create(OtDeadlist, OtDeadlistHeader, 320, 512, 17, 1, FlagUsedBytes);
      var deadlistObj = this.NewObject(mos, deadlist);
      var entries = keys.Select(k => (k.ToString("x"), this.EmptyBpobj())).ToList();
      var (zapSize, zapBlocks) = ZfsWriter.BuildZap(entries);
      this.WriteContents(mos, deadlist, zapBlocks, zapSize);

      W64(head, 8, snapObj);
      W64(head, 16, this.Txg);
      W64(head, 64, deadlistObj);
      W64(head, 96, 0);
      W64(head, 120, U64(head, 120) | DsFlagUniqueAccurate);
      this.RewriteZap(mos, U64(head, 32), names => names.Add((op.Name, snapObj)));
      this.SnapshotsChanged(op.Dir, +1);
    }

    // ── destroy ────────────────────────────────────────────────────────

    /// <summary>
    /// Destroys a snapshot S between its previous snapshot P and its next N (a snapshot or the head):
    /// what N's deadlist holds born after S's previous snapshot was S's alone and is freed; S's own
    /// deadlist merges into N's; sizes follow as OpenZFS keeps them.
    /// </summary>
    private void PlanDestroySnapshot(SnapshotOp op) {
      var mos = this._mos;
      var old = mos.Old;
      var snapObj = this.SnapshotObject(op.Head, op.Name);
      var snapOld = old.RequireDnode(snapObj, "snapshot").Bonus;
      var prevObj = U64(snapOld, 8);
      var s0 = U64(snapOld, 16);
      var nextObj = U64(snapOld, 24);
      var s1 = U64(snapOld, 56);
      var snapDeadlist = U64(snapOld, 64);
      var name = $"{op.Dataset}@{op.Name}";
      if (U64(snapOld, 40) > 1 || this.ZapHasEntries(U64(snapOld, 256)))
        throw new NotSupportedException($"ZFS: snapshot '{name}' has clones; it is not destroyed.");
      if ((U64(snapOld, 120) & DsFlagCaseInsensitive) != 0)
        throw new NotSupportedException($"ZFS: snapshot '{name}' is of a case-insensitive dataset; it is not destroyed here.");
      if (this.ZapHasEntries(U64(old.RequireDnode(op.Dir, "DSL directory").Bonus, 144)))
        throw new NotSupportedException($"ZFS: dataset '{op.Dataset}' has clones; its snapshots are not destroyed.");
      if (old.RequireDnode(snapDeadlist, "deadlist").Type != OtDeadlist || old.RequireDnode(U64(old.RequireDnode(nextObj, "DSL dataset").Bonus, 64), "deadlist").Type != OtDeadlist)
        throw new NotSupportedException("ZFS: old-format deadlists are not merged here.");

      var next = this.Load(mos, nextObj);
      mos.Dirty.Add(nextObj);
      var nb = next.Bonus;
      var nextIsHead = nextObj == op.Head;

      // P's next (or its clone list) moves on to N
      var afterBranch = false;
      Dnode? prev = null;
      if (prevObj != 0) {
        prev = this.Load(mos, prevObj);
        mos.Dirty.Add(prevObj);
        afterBranch = U64(prev.Bonus, 24) != snapObj;
        if (!afterBranch) W64(prev.Bonus, 24, nextObj);
        else if (U64(prev.Bonus, 256) is var clones and not 0) this.ReplaceClone(clones, snapObj, nextObj);
      }
      W64(nb, 8, prevObj);
      W64(nb, 16, s0);

      var nextDl = this.OpenDeadlist(U64(nb, 64));

      // bookmarks: those between P and S now count up to N, so they gain what N's deadlist holds
      // born by their txg; those at S lose "snapshot exists" and keep S's deadlist key alive
      var (bookmarksObj, bookmarks) = this.Bookmarks(op.Head);
      var bookmarkAtSnapshot = false;
      var bookmarkUpdates = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
      foreach (var bookmark in bookmarks.Where(CountsFrees)) {
        var txg = BookmarkTxg(bookmark);
        var values = bookmark.Values.ToArray();
        if (txg >= s0 && txg < s1) {
          var gained = this.DeadlistSpace(nextDl, 0, txg);
          values[8] += (ulong)gained.Used;
          values[9] += (ulong)gained.Compressed;
          values[10] += (ulong)gained.Uncompressed;
        } else if (txg == s1) {
          values[4] &= ~BookmarkSnapshotExists;
          bookmarkAtSnapshot = true;
        } else
          continue;
        bookmarkUpdates[bookmark.Name] = values;
      }
      this.UpdateBookmarks(bookmarksObj, bookmarkUpdates);
      // what P and S shared and N no longer references is P's alone now
      if (prev != null && !afterBranch)
        Add64(prev.Bonus, 96, this.DeadlistSpace(nextDl, U64(prev.Bonus, 16), s0).Used);
      // what S alone held leaves the directory's snapshot usage and is freed
      var freed = this.DeadlistSpace(nextDl, s0, ulong.MaxValue);
      this.Transfer(op.Dir, -freed.Used, -freed.Compressed, -freed.Uncompressed, -freed.Used, UsedHead, UsedSnap);
      foreach (var key in nextDl.Entries.Keys.Where(k => k >= s0).ToList()) {
        var obj = nextDl.Entries[key];
        var space = this.BpobjSpace(obj);
        nextDl.Used -= space.Used;
        nextDl.Compressed -= space.Compressed;
        nextDl.Uncompressed -= space.Uncompressed;
        nextDl.Entries.Remove(key);
        this.ReleaseBpobj(obj, freeBlocks: true);
      }
      // S's deadlist goes into N's
      foreach (var e in old.Zap(snapDeadlist, "deadlist"))
        this.DeadlistEnqueue(nextDl, Convert.ToUInt64(e.Name, 16), e.Value, addSpace: true);
      this.FreeObject(mos, snapDeadlist);
      this.WriteDeadlist(nextDl);

      if (!nextIsHead) {
        // N gains what only S and N shared: born after S's previous snapshot, up to S, dead before N's next
        var nextNext = old.RequireDnode(U64(nb, 24), "DSL dataset");
        var nnDl = this.OpenDeadlist(U64(nextNext.Bonus, 64));
        Add64(nb, 96, this.DeadlistSpace(nnDl, s0, s1).Used);
        // the head's deadlist loses S's key, its entry merged into the one before
        var headDl = this.OpenDeadlist(U64(this.Load(mos, op.Head).Bonus, 64));
        if (!bookmarkAtSnapshot && headDl.Entries.TryGetValue(s1, out var entryObj)) {
          headDl.Entries.Remove(s1);
          this.DeadlistEnqueue(headDl, s1, entryObj, addSpace: false);
          this.WriteDeadlist(headDl);
        }
      } else {
        // the head's unique bytes: what it references minus what its previous snapshot still shares
        var mrs = prev == null ? 0 : U64(prev.Bonus, 72);
        W64(nb, 96, (ulong)((long)U64(nb, 72) - ((long)mrs - nextDl.Used)));
        W64(nb, 120, U64(nb, 120) | DsFlagUniqueAccurate);
      }

      foreach (var f in this.DatasetFeatureEntries(snapObj)) this.AdjustFeature(f.Name, -1);
      this.RewriteZap(mos, U64(this.Load(mos, op.Head).Bonus, 32), names => names.RemoveAll(n => n.Name == op.Name));
      this.SnapshotsChanged(op.Dir, -1);
      // user holds go with the snapshot: its holds object below, and its temporary holds ("<hex object>-<tag>")
      var tmpHolds = old.Zap(1, "MOS object directory").FirstOrDefault(e => e.Name == "tmp_userrefs")?.Value ?? 0;
      var holdPrefix = snapObj.ToString("x") + "-";
      if (tmpHolds != 0 && old.Zap(tmpHolds, "temporary holds").Any(e => e.Name.StartsWith(holdPrefix, StringComparison.Ordinal)))
        this.RewriteZap(mos, tmpHolds, holds => holds.RemoveAll(e => e.Name.StartsWith(holdPrefix, StringComparison.Ordinal)));
      foreach (var at in new[] { 256, 264, 272 })                   // next clones, properties, user holds
        if (U64(snapOld, at) is var obj and not 0) this.FreeObject(mos, obj);
      this.FreeObject(mos, snapObj);
      this.TouchDirChain(op.Dir);
    }

    // ── bookmarks ──────────────────────────────────────────────────────

    /// <summary>A head's bookmark ZAP and its entries (name → 3 or 12 words of zfs_bookmark_phys_t).</summary>
    private (ulong Object, List<ZfsZapEntry> Entries) Bookmarks(ulong head) {
      var obj = this.DatasetZapValue(head, BookmarksField);
      return obj == 0 ? (0, []) : (obj, this._mos.Old.Zap(obj, "bookmarks"));
    }

    private static bool CountsFrees(ZfsZapEntry bookmark) => bookmark.Values.Length >= 11 && (bookmark.Values[4] & BookmarkHasFbn) != 0;
    private static ulong BookmarkTxg(ZfsZapEntry bookmark) => bookmark.Values.Length >= 2 ? bookmark.Values[1] : 0;

    /// <summary>Starts counting what a filesystem's edit frees when a bookmark at or after its previous snapshot keeps that count.</summary>
    private void TrackBookmarks(Fs fs, ulong prevSnapTxg) {
      var (obj, entries) = this.Bookmarks(fs.HeadObject);
      fs.BookmarksObject = obj;
      fs.CountingBookmarks = entries.Where(b => CountsFrees(b) && BookmarkTxg(b) >= prevSnapTxg).ToList();
      if (fs.CountingBookmarks.Count > 0) fs.Os.Kills = [];
    }

    /// <summary>
    /// A bookmark whose next snapshot is the head counts the space the head frees of what was
    /// born by the bookmark's txg (its "freed before next snapshot" words 8-10).
    /// </summary>
    private void PlanBookmarkKills(Fs fs) {
      var updated = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
      foreach (var bookmark in fs.CountingBookmarks) {
        var txg = BookmarkTxg(bookmark);
        var values = bookmark.Values.ToArray();
        foreach (var (birth, used, compressed, uncompressed) in fs.Os.Kills!) {
          if (birth > txg) continue;
          values[8] += (ulong)used;
          values[9] += (ulong)compressed;
          values[10] += (ulong)uncompressed;
        }
        updated[bookmark.Name] = values;
      }
      this.UpdateBookmarks(fs.BookmarksObject, updated);
    }

    private void UpdateBookmarks(ulong obj, Dictionary<string, ulong[]> updated) {
      if (updated.Count == 0) return;
      this.EditZap(this._mos, obj, entries => {
        for (var i = 0; i < entries.Count; ++i)
          if (updated.TryGetValue(entries[i].Name, out var values)) entries[i] = entries[i] with { Values = values };
      });
    }

    // ── deadlists ──────────────────────────────────────────────────────

    private sealed class DeadlistState {
      public ulong Object;
      public SortedDictionary<ulong, ulong> Entries = [];
      public long Used, Compressed, Uncompressed;                     // changes to the header
    }

    private DeadlistState OpenDeadlist(ulong obj) {
      var state = new DeadlistState { Object = obj };
      foreach (var e in this._mos.Old.Zap(obj, "deadlist")) state.Entries[Convert.ToUInt64(e.Name, 16)] = e.Value;
      return state;
    }

    /// <summary>Space of the entries whose key lies in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private (long Used, long Compressed, long Uncompressed) DeadlistSpace(DeadlistState dl, ulong from, ulong to) {
      long u = 0, c = 0, x = 0;
      foreach (var (key, obj) in dl.Entries) {
        if (key < from || key >= to) continue;
        var space = this.BpobjSpace(obj);
        u += space.Used;
        c += space.Compressed;
        x += space.Uncompressed;
      }
      return (u, c, x);
    }

    /// <summary>
    /// Puts a bpobj under the entry with the largest key at or below <paramref name="key"/>: it
    /// replaces an empty entry, an empty one is dropped, else its block pointers join the entry's.
    /// </summary>
    private void DeadlistEnqueue(DeadlistState dl, ulong key, ulong obj, bool addSpace) {
      var empty = this.OldEmptyBpobj();
      var targetKey = dl.Entries.Keys.Where(k => k <= key).DefaultIfEmpty(ulong.MaxValue).Max();
      if (targetKey == ulong.MaxValue) throw new InvalidDataException("ZFS: a deadlist has no entry for a merged one.");
      var space = this.BpobjSpace(obj);
      if (addSpace) {
        dl.Used += space.Used;
        dl.Compressed += space.Compressed;
        dl.Uncompressed += space.Uncompressed;
      }
      var target = dl.Entries[targetKey];
      if (target == empty && empty != 0) {
        --this._emptyBpobjDelta;
        dl.Entries[targetKey] = obj;
        return;
      }
      if (obj == empty && empty != 0) {
        --this._emptyBpobjDelta;
        return;
      }
      var pointers = this.BpobjPointers(obj);
      if (pointers.Count > 0) {
        var d = this.Load(this._mos, target);
        this.AppendToBpobj(target, d, pointers, space.Used, space.Compressed, space.Uncompressed);
      }
      this.ReleaseBpobj(obj, freeBlocks: false);
    }

    private void WriteDeadlist(DeadlistState dl) {
      var entries = dl.Entries.Select(e => new ZfsZapEntry(e.Key.ToString("x"), 8, [e.Value])).ToList();
      this.EditZap(this._mos, dl.Object, list => {
        list.Clear();
        list.AddRange(entries);
      });
      var d = this.Load(this._mos, dl.Object);
      this._mos.Dirty.Add(dl.Object);
      Add64(d.Bonus, 0, dl.Used);
      Add64(d.Bonus, 8, dl.Compressed);
      Add64(d.Bonus, 16, dl.Uncompressed);
    }

    // ── bpobjs ─────────────────────────────────────────────────────────

    /// <summary>A bpobj's bytes (its sub-objects included), as its header and this plan's appends have them.</summary>
    private (long Used, long Compressed, long Uncompressed) BpobjSpace(ulong obj) {
      var bonus = this._mos.Old.RequireDnode(obj, "bpobj").Bonus;
      long u = (long)U64(bonus, 8), c = (long)U64(bonus, 16), x = (long)U64(bonus, 24);
      if (this._bpobjAppends.TryGetValue(obj, out var a)) {
        u += a.Used;
        c += a.Compressed;
        x += a.Uncompressed;
      }
      return (u, c, x);
    }

    /// <summary>Every block pointer a bpobj holds, its sub-objects' included.</summary>
    private List<byte[]> BpobjPointers(ulong obj, int depth = 0) {
      if (depth > 32) throw new InvalidDataException("ZFS: bpobj sub-objects nest too deeply.");
      if (this._bpobjAppends.ContainsKey(obj)) throw new InvalidOperationException("ZFS: a bpobj is read after this plan changed it.");
      var old = this._mos.Old;
      var dnode = old.RequireDnode(obj, "bpobj");
      var bonus = dnode.Bonus;
      if (bonus.Length >= 56 && U64(bonus, 48) != 0)
        throw new NotSupportedException("ZFS: a bpobj with freed entries (livelists) is not merged here.");
      var pointers = new List<byte[]>();
      var count = U64(bonus, 0);
      var perBlock = (ulong)(dnode.DataBlockSize / Bp);
      byte[]? block = null;
      var blockId = ulong.MaxValue;
      for (ulong i = 0; i < count; ++i) {
        if (i / perBlock != blockId) {
          blockId = i / perBlock;
          block = old.ReadBlock(dnode, blockId);
        }
        pointers.Add(block!.AsSpan((int)(i % perBlock) * Bp, Bp).ToArray());
      }
      if (bonus.Length >= 48 && U64(bonus, 32) != 0)
        foreach (var sub in this.SubObjects(dnode))
          pointers.AddRange(this.BpobjPointers(sub, depth + 1));
      return pointers;
    }

    private List<ulong> SubObjects(ZfsDnode bpobj) {
      var old = this._mos.Old;
      var bonus = bpobj.Bonus;
      var list = old.RequireDnode(U64(bonus, 32), "bpobj sub-object list");
      var count = U64(bonus, 40);
      var perBlock = (ulong)(list.DataBlockSize / 8);
      var objects = new List<ulong>();
      for (ulong i = 0; i < count; ++i)
        objects.Add(U64(old.ReadBlock(list, i / perBlock), (int)(i % perBlock) * 8));
      return objects;
    }

    /// <summary>
    /// Lets go of a bpobj: the shared empty one loses a reference, any other is freed with its
    /// sub-objects — and, when <paramref name="freeBlocks"/>, every block pointer it holds.
    /// </summary>
    private void ReleaseBpobj(ulong obj, bool freeBlocks, int depth = 0) {
      if (depth > 32) throw new InvalidDataException("ZFS: bpobj sub-objects nest too deeply.");
      if (obj == this.OldEmptyBpobj() && obj != 0) {
        --this._emptyBpobjDelta;
        return;
      }
      var dnode = this._mos.Old.RequireDnode(obj, "bpobj");
      var bonus = dnode.Bonus;
      if (freeBlocks) {
        if (bonus.Length >= 56 && U64(bonus, 48) != 0)
          throw new NotSupportedException("ZFS: a bpobj with freed entries (livelists) is not freed here.");
        var count = U64(bonus, 0);
        var perBlock = (ulong)(dnode.DataBlockSize / Bp);
        for (ulong i = 0; i < count; ++i) {
          var raw = this._mos.Old.ReadBlock(dnode, i / perBlock).AsSpan((int)(i % perBlock) * Bp, Bp).ToArray();
          this.FreeStoredPointer(raw);
        }
      }
      if (bonus.Length >= 48 && U64(bonus, 32) != 0) {
        foreach (var sub in this.SubObjects(dnode)) this.ReleaseBpobj(sub, freeBlocks, depth + 1);
        this.FreeObject(this._mos, U64(bonus, 32));
      }
      this.FreeObject(this._mos, obj);
    }

    /// <summary>Frees a block a bpobj recorded: nothing for a hole or embedded one, a reference for a deduplicated one.</summary>
    private void FreeStoredPointer(byte[] raw) {
      if (IsHole(raw) || IsEmbedded(raw)) return;
      if (((U64(raw, 0x30) >> 62) & 1) != 0) {
        this._dedupFrees.Add(raw);
        return;
      }
      var bp = ZfsBlockPointer.Read(raw);
      if (this.BrtKeeps(bp)) return;
      this.FreeSpace(bp, 0);
    }

    /// <summary>Queues block pointers (as bpobjs store them) for the end of a bpobj; <see cref="FlushBpobjAppends"/> writes them.</summary>
    private void AppendToBpobj(ulong obj, Dnode d, List<byte[]> stored, long used, long compressed, long uncompressed) {
      if (this._bpobjAppends.TryGetValue(obj, out var a)) {
        a.Stored.AddRange(stored);
        this._bpobjAppends[obj] = (a.Dnode, a.Stored, a.Used + used, a.Compressed + compressed, a.Uncompressed + uncompressed);
      } else
        this._bpobjAppends[obj] = (d, stored.ToList(), used, compressed, uncompressed);
    }

    private void FlushBpobjAppends() {
      var mos = this._mos;
      foreach (var (obj, (d, stored, used, compressed, uncompressed)) in this._bpobjAppends) {
        mos.Dirty.Add(obj);
        var bonus = d.Bonus;
        var count = U64(bonus, 0);
        var blockSize = d.DataBlockSize;
        var perBlock = (ulong)(blockSize / Bp);
        var changes = new Dictionary<ulong, byte[]>();
        foreach (var raw in stored) {
          var blockId = count / perBlock;
          if (!changes.TryGetValue(blockId, out var data)) {
            data = new byte[blockSize];
            if (!d.IsNew && count % perBlock != 0) {
              var oldDnode = mos.Old.RequireDnode(obj, "bpobj");
              if (blockId <= oldDnode.MaxBlockId) mos.Old.ReadBlock(oldDnode, blockId).AsSpan(0, blockSize).CopyTo(data);
            }
            changes[blockId] = data;
          }
          raw.CopyTo(data.AsSpan((int)(count % perBlock) * Bp));
          ++count;
        }
        W64(bonus, 0, count);
        Add64(bonus, 8, used);
        Add64(bonus, 16, compressed);
        Add64(bonus, 24, uncompressed);
        var blocks = new Dictionary<ulong, NewBlock?>();
        foreach (var (blockId, data) in changes) {
          var block = this.AllocBlock(mos, d, blockSize, OtBpobj, 0);
          block.Data = data;
          blocks[blockId] = block;
        }
        this.SetBlocks(mos, d, blocks);
      }
      this._bpobjAppends.Clear();
    }

    // ── the shared empty bpobj ─────────────────────────────────────────

    private ulong OldEmptyBpobj() =>
      this._mos.Old.Zap(1, "MOS object directory").FirstOrDefault(e => e.Name == "empty_bpobj")?.Value ?? 0;

    /// <summary>An empty bpobj for a new deadlist entry: the shared one (created when the pool has none) or, without empty_bpobj, a new one.</summary>
    private ulong EmptyBpobj() {
      var mos = this._mos;
      if (!this._s.IsEnabled("com.delphix:empty_bpobj"))
        return this.NewObject(mos, Dnode.Create(OtBpobj, OtBpobjHeader, 48, BpobjBlock, 17, 1, FlagUsedBytes));
      ++this._emptyBpobjDelta;
      if (this.OldEmptyBpobj() is var existing and not 0) return existing;
      if (this._emptyBpobjNew == 0) {
        this._emptyBpobjNew = this.NewObject(mos, Dnode.Create(OtBpobj, OtBpobjHeader, 48, BpobjBlock, 17, 1, FlagUsedBytes));
        var created = this._emptyBpobjNew;
        this.RewriteZap(mos, 1, entries => entries.Add(("empty_bpobj", created)));
      }
      return this._emptyBpobjNew;
    }

    /// <summary>empty_bpobj counts the deadlist entries sharing the empty bpobj; at 0 that object and its directory entry go.</summary>
    private void AdjustEmptyBpobj() {
      var mos = this._mos;
      var count = mos.Old.Zap(this._s.FeaturesForWrite, "features_for_write").FirstOrDefault(e => e.Name == "com.delphix:empty_bpobj")
        ?? throw new InvalidDataException("ZFS: empty_bpobj is not in the write list.");
      var remaining = (long)count.Value + this._emptyBpobjDelta;
      if (remaining < 0) throw new InvalidDataException("ZFS: empty_bpobj would count fewer than no references.");
      this.RewriteZap(mos, this._s.FeaturesForWrite, entries => {
        var at = entries.FindIndex(e => e.Name == "com.delphix:empty_bpobj");
        entries[at] = (entries[at].Name, (ulong)remaining);
      });
      var existing = this.OldEmptyBpobj();
      if (remaining != 0 || existing == 0) return;
      this.RewriteZap(mos, 1, entries => entries.RemoveAll(e => e.Name == "empty_bpobj"));
      this.FreeObject(mos, existing);
    }

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>The entries of a (zapified) dataset object that name enabled features: the per-dataset features it uses.</summary>
    private List<ZfsZapEntry> DatasetFeatureEntries(ulong dataset) {
      var d = this._mos.Old.RequireDnode(dataset, "DSL dataset");
      return d.Type != OtZapMetadata ? [] : this._mos.Old.Zap(d).Where(e => this._s.Features.ContainsKey(e.Name)).ToList();
    }

    private ulong DatasetZapValue(ulong dataset, string name) {
      var d = this._mos.Old.RequireDnode(dataset, "DSL dataset");
      return d.Type != OtZapMetadata ? 0 : this._mos.Old.Zap(d).FirstOrDefault(e => e.Name == name)?.Value ?? 0;
    }

    private bool ZapHasEntries(ulong obj) => obj != 0 && this._mos.Old.Zap(obj, "ZAP").Count > 0;

    /// <summary>Changes a feature's reference count in whichever list holds it.</summary>
    private void AdjustFeature(string guid, long delta) {
      if (!this._s.Features.TryGetValue(guid, out var feature)) throw new InvalidDataException($"ZFS: feature {guid} is not enabled.");
      this.RewriteZap(this._mos, feature.ForWrite ? this._s.FeaturesForWrite : this._s.FeaturesForRead, entries => {
        var at = entries.FindIndex(e => e.Name == guid);
        var value = (long)entries[at].Value + delta;
        if (value < 0) throw new InvalidDataException($"ZFS: feature {guid} would count fewer than no references.");
        entries[at] = (guid, (ulong)value);
      });
    }

    /// <summary>A clone list (keys: object numbers in hex) names <paramref name="newObj"/> where it named <paramref name="oldObj"/>.</summary>
    private void ReplaceClone(ulong clones, ulong oldObj, ulong newObj) =>
      this.RewriteZap(this._mos, clones, entries => {
        entries.RemoveAll(e => e.Name == oldObj.ToString("x"));
        entries.Add((newObj.ToString("x"), newObj));
      });

    /// <summary>
    /// A directory's snapshot count (and its ancestors', as far as each keeps one) and, with
    /// extensible_dataset, the time its snapshots last changed (the directory turned into a ZAP if need be).
    /// </summary>
    private void SnapshotsChanged(ulong dir, long delta) {
      var mos = this._mos;
      var d = dir;
      for (var guard = 0; d != 0 && guard < 64; ++guard) {
        var node = mos.Old.RequireDnode(d, "DSL directory");
        if (node.Type != OtZapMetadata || mos.Old.Zap(node).All(e => e.Name != SnapshotCountField)) break;
        this.RewriteZap(mos, d, entries => {
          var at = entries.FindIndex(e => e.Name == SnapshotCountField);
          entries[at] = (SnapshotCountField, (ulong)((long)entries[at].Value + delta));
        });
        d = U64(node.Bonus, 16);
      }
      if (!this._s.IsEnabled("com.delphix:extensible_dataset")) return;
      var now = new DateTimeOffset(this._now);
      var stamp = new ZfsZapEntry(SnapshotsChangedField, 8, [(ulong)now.ToUnixTimeSeconds(), (ulong)(now.UtcTicks % TimeSpan.TicksPerSecond * 100)]);
      if (mos.Old.RequireDnode(dir, "DSL directory").Type == OtZapMetadata) {
        this.EditZap(mos, dir, entries => {
          entries.RemoveAll(e => e.Name == SnapshotsChangedField);
          entries.Add(stamp);
        });
        return;
      }
      var dnode = this.Load(mos, dir);
      mos.Dirty.Add(dir);
      dnode.Raw[0] = OtZapMetadata;
      var (blockSize, blocks) = ZfsWriter.BuildZap([stamp], fat: true);
      this.WriteContents(mos, dnode, blocks, blockSize);
    }

    private static ulong NewGuid() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) | 1;
  }
}
