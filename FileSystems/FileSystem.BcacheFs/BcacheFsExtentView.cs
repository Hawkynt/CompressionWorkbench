#pragma warning disable CS1591
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Materialized logical extent ranges after composing the log-structured bsets
/// and the recovery journal. Fragments never rewrite the source bkey: a trimmed
/// checksummed/compressed extent still names its original physical bounds.
/// </summary>
internal sealed class BcacheFsExtentView {
  private readonly Dictionary<BcacheFsExtentFile, IReadOnlyList<BcacheFsExtentFragment>> _files;

  internal required IReadOnlyList<string> Diagnostics { get; init; }
  internal required bool Complete { get; init; }

  private BcacheFsExtentView(
      Dictionary<BcacheFsExtentFile, IReadOnlyList<BcacheFsExtentFragment>> files)
    => this._files = files;

  internal IReadOnlyList<BcacheFsExtentFragment> Fragments(ulong inode, uint snapshot)
    => this._files.GetValueOrDefault(new BcacheFsExtentFile(inode, snapshot)) ?? [];

  internal IEnumerable<BcacheFsExtentFile> Files => this._files.Keys;

  internal static BcacheFsExtentView Build(BcacheFsCoreVolume volume) {
    ArgumentNullException.ThrowIfNull(volume);
    var tree = volume.ReadTree(BcacheFsBtreeId.Extents);
    var diagnostics = new List<string>(tree.Diagnostics);
    var complete = tree.Complete;

    // First resolve exact bkey slots. Deleted keys override an older key at the
    // same position; surviving keys retain the order of their last write so
    // overlapping extents at different end positions can then be composed by
    // chronology rather than by key sort order.
    var slots = new Dictionary<Bpos, BcacheFsLayeredExtentKey>();
    long order = 0;
    foreach (var node in tree.Nodes.Where(n => n.Level == 0)) {
      foreach (var set in node.Sets) {
        if (!set.Visible) continue;
        foreach (var key in set.Keys) {
          if (key.Type == BcacheFsKeyType.Deleted)
            slots.Remove(key.Position);
          else
            slots[key.Position] = new BcacheFsLayeredExtentKey(key, order++);
        }
      }
    }

    foreach (var update in volume.Overlay.Keys((byte)BcacheFsBtreeId.Extents, 0)
      .OrderBy(k => k.Sequence)
      .ThenBy(k => k.JournalOrder)) {
      if (update.Key.Type == BcacheFsKeyType.Deleted)
        slots.Remove(update.Key.Position);
      else
        slots[update.Key.Position] = new BcacheFsLayeredExtentKey(update.Key, order++);
    }

    var files = new Dictionary<BcacheFsExtentFile, IReadOnlyList<BcacheFsExtentFragment>>();
    foreach (var group in slots.Values
      .GroupBy(k => new BcacheFsExtentFile(k.Key.Position.Inode, k.Key.Position.Snapshot))) {
      var fragments = new List<BcacheFsExtentFragment>();
      foreach (var layered in group.OrderBy(k => k.Order)) {
        var key = layered.Key;
        if (key.Size == 0) {
          // Cookie keys are position markers used by reconcile, not file ranges.
          if (key.Type is not (BcacheFsKeyType.Cookie or BcacheFsKeyType.Whiteout)) {
            diagnostics.Add($"extents btree key type {key.RawType} at {Format(key.Position)} has zero range size.");
            complete = false;
          }
          continue;
        }
        if (key.Size > key.Position.Offset) {
          diagnostics.Add($"extent at {Format(key.Position)} has size {key.Size} larger than its end offset.");
          complete = false;
          continue;
        }

        if (!TryClassify(key, out var kind)) {
          diagnostics.Add($"extents btree key type {key.RawType} at {Format(key.Position)} has no range semantics implemented.");
          complete = false;
          continue;
        }

        var sourceStart = key.Position.Offset - key.Size;
        var sourceEnd = key.Position.Offset;
        Overlay(fragments, new BcacheFsExtentFragment(
          sourceStart,
          sourceEnd,
          sourceStart,
          kind,
          key,
          layered.Order));
      }

      files[group.Key] = Coalesce(fragments);
    }

    return new BcacheFsExtentView(files) {
      Diagnostics = diagnostics,
      Complete = complete,
    };
  }

  private static bool TryClassify(BcacheFsRawKey key, out BcacheFsExtentFragmentKind kind) {
    switch (key.Type) {
      case BcacheFsKeyType.Extent:
        kind = BcacheFsExtentFragmentKind.Data;
        return true;
      case BcacheFsKeyType.Reservation:
        kind = BcacheFsExtentFragmentKind.Reservation;
        return true;
      case BcacheFsKeyType.ReflinkP:
        kind = BcacheFsExtentFragmentKind.Reflink;
        return true;
      case BcacheFsKeyType.InlineData:
        kind = BcacheFsExtentFragmentKind.InlineData;
        return true;
      case BcacheFsKeyType.Error:
        kind = BcacheFsExtentFragmentKind.Error;
        return true;
      case BcacheFsKeyType.ExtentWhiteout:
      case BcacheFsKeyType.Whiteout:
        kind = BcacheFsExtentFragmentKind.Whiteout;
        return true;
      default:
        kind = default;
        return false;
    }
  }

  /// <summary>
  /// Places a newer range over an existing non-overlapping view. Old fragments
  /// are sliced logically only; SourceStartSector and SourceKey remain unchanged.
  /// </summary>
  internal static void Overlay(
      List<BcacheFsExtentFragment> fragments,
      BcacheFsExtentFragment newer) {
    if (newer.StartSector >= newer.EndSector)
      return;

    var next = new List<BcacheFsExtentFragment>(fragments.Count + 2);
    foreach (var old in fragments) {
      if (old.EndSector <= newer.StartSector || old.StartSector >= newer.EndSector) {
        next.Add(old);
        continue;
      }

      if (old.StartSector < newer.StartSector)
        next.Add(old with { EndSector = newer.StartSector });
      if (old.EndSector > newer.EndSector)
        next.Add(old with { StartSector = newer.EndSector });
    }

    next.Add(newer);
    next.Sort(static (a, b) => a.StartSector.CompareTo(b.StartSector));
    fragments.Clear();
    fragments.AddRange(next);
  }

  private static IReadOnlyList<BcacheFsExtentFragment> Coalesce(
      List<BcacheFsExtentFragment> fragments) {
    if (fragments.Count < 2) return fragments.ToArray();
    fragments.Sort(static (a, b) => a.StartSector.CompareTo(b.StartSector));
    var result = new List<BcacheFsExtentFragment> { fragments[0] };
    for (var i = 1; i < fragments.Count; ++i) {
      var previous = result[^1];
      var current = fragments[i];
      var previousSourceOffset = previous.EndSector - previous.SourceStartSector;
      var currentSourceOffset = current.StartSector - current.SourceStartSector;
      if (previous.EndSector == current.StartSector
          && ReferenceEquals(previous.SourceKey, current.SourceKey)
          && previous.Kind == current.Kind
          && previousSourceOffset == currentSourceOffset) {
        result[^1] = previous with { EndSector = current.EndSector };
      } else {
        result.Add(current);
      }
    }
    return result;
  }

  private static string Format(Bpos position)
    => $"{position.Inode}:{position.Offset}:{position.Snapshot}";
}

internal readonly record struct BcacheFsExtentFile(ulong Inode, uint Snapshot);

internal enum BcacheFsExtentFragmentKind : byte {
  Data,
  Reservation,
  Reflink,
  InlineData,
  Error,
  Whiteout,
}

/// <summary>
/// One live logical interval. SourceStartSector is the start of the original
/// source bkey, not necessarily StartSector after later overwrites trimmed it.
/// </summary>
internal sealed record BcacheFsExtentFragment(
  ulong StartSector,
  ulong EndSector,
  ulong SourceStartSector,
  BcacheFsExtentFragmentKind Kind,
  BcacheFsRawKey SourceKey,
  long LayerOrder) {
  internal ulong SectorCount => this.EndSector - this.StartSector;
  internal ulong SourceOffsetSectors => this.StartSector - this.SourceStartSector;
}

internal sealed record BcacheFsLayeredExtentKey(BcacheFsRawKey Key, long Order);
