#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>
/// Lays a <see cref="Reiser4Plan" /> out as a volume: data blocks for extents, then a node40 (or
/// node41) tree of stat data, directory entries and tail items in leaves, extents in twigs, and as
/// many internal levels above them as it takes — plus the allocator bitmaps and the counters the
/// format superblock keeps.
/// </summary>
/// <remarks>
/// <para>A node is a 28-byte header (32 for node41, whose last four bytes are a CRC-32C of the
/// rest), item bodies growing up from there, and an array of item headers growing down from the
/// node's end — each a key (four words, or three on a short-key volume: 38 or 30 bytes a header),
/// the body's offset, flags, and the item plugin.
/// Items sit in key order. A leaf never straddles a twig extent: the leaf run is ended before an
/// extent's key and resumed after it.</para>
/// </remarks>
internal static class Reiser4Tree {

  internal const int NodeHeaderBytes = 28;
  /// <summary>An item header on a volume of large keys (<see cref="Reiser4KeyShape.ItemHeaderBytes" /> in general).</summary>
  internal const int ItemHeaderBytes = 38;
  internal const int NodeMagic = 0x52344653;

  private sealed record Item(Reiser4Key Key, ushort Plugin, byte[] Body);

  /// <summary>What a laid-out volume amounts to.</summary>
  internal sealed record Result(ulong Blocks, ushort Height, ulong ObjectCount, IReadOnlyList<ulong> NodeBlocks);

  /// <summary>Lays <paramref name="plan" /> out and writes the whole volume to <paramref name="output" />.</summary>
  internal static Result Write(Reiser4Plan plan, Stream output) => WriteReserving(plan, output, plan.MinimumBlocks);

  /// <param name="reserveFor">The volume length whose superblock copies are kept free while laying
  /// out. Copies only ever get added as a volume grows, so when the layout outgrows it and a copy
  /// for the final length lands among the blocks already handed out, the layout is redone for that
  /// length before anything is written.</param>
  private static Result WriteReserving(Reiser4Plan plan, Stream output, ulong reserveFor) {
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(output);
    var layout = plan.Layout;
    var blockSize = layout.BlockSize;
    var headerBytes = plan.NodePlugin == 1 ? 32 : NodeHeaderBytes;
    var capacity = blockSize - headerBytes;
    var keys = plan.Keys;
    var maxBody = capacity - keys.ItemHeaderBytes;

    var reserved = layout.BackupCopies(reserveFor).ToHashSet();
    var cursor = layout.FirstData;
    bool IsBitmapBlock(ulong block) => layout.IsBitmapBlock(block) || reserved.Contains(block);
    ulong Allocate() {
      while (IsBitmapBlock(cursor)) ++cursor;
      return cursor++;
    }

    // ── Data blocks, in object order ────────────────────────────────────
    var payloads = new DeferredPayloads();
    var items = new List<Item>();
    foreach (var obj in plan.Objects) {
      items.Add(new Item(obj.StatKey, Reiser4ItemPlugin.StatData, obj.Stat));
      if (obj.IsDirectory) {
        items.AddRange(DirectoryItems(obj, maxBody, keys));
        continue;
      }
      foreach (var (key, plugin, body) in obj.Items) {
        if (body.Length > maxBody) throw new InvalidDataException("Reiser4: a body item exceeds node capacity.");
        items.Add(new Item(key, plugin, body));
      }
      if (obj.Extents.Count == 0) continue;

      var units = new List<(ulong Start, ulong Width)>();
      foreach (var unit in obj.Extents) {
        if (unit.Payload is not { } payload) { units.Add((0, unit.Width)); continue; }
        // A run of blocks never includes an allocator bitmap; one that would is split around it.
        ulong done = 0;
        while (done < unit.Width) {
          var start = Allocate();
          ulong width = 1;
          while (done + width < unit.Width && !IsBitmapBlock(cursor)) { ++cursor; ++width; }
          var byteOffset = (long)done * blockSize;
          var byteLength = Math.Min((long)width * blockSize, payload.Size - byteOffset);
          if (byteLength > 0) payloads.Add((long)start * blockSize, Slice(payload, byteOffset, byteLength));
          if (units.Count > 0 && units[^1].Start != 0 && units[^1].Start + units[^1].Width == start)
            units[^1] = (units[^1].Start, units[^1].Width + width);
          else
            units.Add((start, width));
          done += width;
        }
      }
      var perItem = maxBody / 16;
      ulong logical = 0;
      var bodyKey = obj.StatKey;
      for (var first = 0; first < units.Count; first += perItem) {
        var count = Math.Min(perItem, units.Count - first);
        var body = new byte[count * 16];
        for (var i = 0; i < count; ++i) {
          BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(i * 16), units[first + i].Start);
          BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(i * 16 + 8), units[first + i].Width);
        }
        items.Add(new Item(Reiser4Key.Of(bodyKey.Locality, Reiser4Key.MinorBody, bodyKey.Ordering, bodyKey.ObjectId, logical),
          Reiser4ItemPlugin.Extent, body));
        for (var i = 0; i < count; ++i) logical = checked(logical + units[first + i].Width * (ulong)blockSize);
      }
    }

    items.Sort(static (a, b) => a.Key.CompareTo(b.Key));
    for (var i = 1; i < items.Count; ++i)
      if (items[i].Key.CompareTo(items[i - 1].Key) == 0 && items[i].Plugin != Reiser4ItemPlugin.DirEntries)
        throw new InvalidDataException("Reiser4: two items share one key.");

    // ── Leaves and twigs ────────────────────────────────────────────────
    var nodes = new Dictionary<ulong, byte[]>();
    var twigItems = new List<Item>();
    var pending = new List<Item>();
    var firstLeaf = true;
    void FlushLeaves() {
      foreach (var group in Pack(pending, capacity, keys)) {
        var block = firstLeaf ? layout.FirstLeaf : Allocate();
        firstLeaf = false;
        nodes.Add(block, EncodeNode(group, plan, headerBytes, 1));
        twigItems.Add(new Item(group[0].Key, Reiser4ItemPlugin.NodePointer, PointerBody(block)));
      }
      pending.Clear();
    }
    foreach (var item in items) {
      if (item.Plugin == Reiser4ItemPlugin.Extent) {
        FlushLeaves();
        twigItems.Add(item);
      } else pending.Add(item);
    }
    FlushLeaves();

    ushort level = 2;
    var parentItems = twigItems;
    while (true) {
      var groups = Pack(parentItems, capacity, keys).ToList();
      if (groups.Count == 1) {
        nodes.Add(layout.Root, EncodeNode(groups[0], plan, headerBytes, (byte)level));
        break;
      }
      parentItems = [];
      foreach (var group in groups) {
        var block = Allocate();
        nodes.Add(block, EncodeNode(group, plan, headerBytes, (byte)level));
        parentItems.Add(new Item(group[0].Key, Reiser4ItemPlugin.NodePointer, PointerBody(block)));
      }
      level = checked((ushort)(level + 1));
    }

    // ── Counters, bitmaps and the fixed blocks ──────────────────────────
    var used = cursor;
    var blocks = Math.Max(plan.MinimumBlocks, used + 1);
    var copies = layout.BackupCopies(blocks);
    if (copies.Any(c => c < used && !reserved.Contains(c))) return WriteReserving(plan, output, blocks);
    var tailCopies = copies.Where(c => c >= used).ToList();
    var format = plan.Format.ToArray();
    var backup = plan.Backup.ToArray();
    var objects = (ulong)plan.Objects.Count;
    var tailBitmaps = (blocks - 1) / layout.BlocksPerBitmap - (used - 1) / layout.BlocksPerBitmap;
    BinaryPrimitives.WriteUInt64LittleEndian(format.AsSpan(0), blocks);
    BinaryPrimitives.WriteUInt64LittleEndian(format.AsSpan(8), blocks - used - tailBitmaps - (ulong)tailCopies.Count);
    BinaryPrimitives.WriteUInt64LittleEndian(format.AsSpan(16), layout.Root);
    BinaryPrimitives.WriteUInt64LittleEndian(format.AsSpan(24), plan.NextObjectId);
    BinaryPrimitives.WriteUInt64LittleEndian(format.AsSpan(32), objects);
    BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(48), plan.MkfsId);
    BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(68), level);
    BinaryPrimitives.WriteUInt64LittleEndian(backup.AsSpan(0x4D), blocks);
    BinaryPrimitives.WriteUInt32LittleEndian(backup.AsSpan(0x55), plan.MkfsId);

    var basePosition = output.Position;
    var zero = new byte[blockSize];
    var bitmaps = BuildBitmaps(layout, blocks, used, tailCopies);
    for (var b = 0UL; b < layout.FirstData; ++b) {
      var bytes = b == layout.Master ? plan.Master
        : b == layout.Format ? format
        : b == layout.FirstBitmap ? bitmaps[0].Bytes
        : b == layout.Status ? plan.Status
        : b == layout.Backup ? backup
        : b == layout.Root || b == layout.FirstLeaf ? nodes[b]
        : zero;
      output.Write(bytes, 0, blockSize);
    }
    output.SetLength(basePosition + checked((long)blocks * blockSize));
    foreach (var (block, bytes) in bitmaps.Skip(1)) {
      output.Position = basePosition + (long)block * blockSize;
      output.Write(bytes);
    }
    foreach (var copy in copies) {
      output.Position = basePosition + checked((long)copy * blockSize);
      output.Write(backup, 0, blockSize);
    }
    foreach (var (block, node) in nodes) {
      if (block == layout.Root || block == layout.FirstLeaf) continue;
      output.Position = basePosition + checked((long)block * blockSize);
      output.Write(node);
    }
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + (long)blocks * blockSize;
    output.Flush();
    return new Result(blocks, level, objects, [.. nodes.Keys]);
  }

  /// <summary>The cde40 items of a directory: its entries in key order, packed into items that fit a node.</summary>
  private static IEnumerable<Item> DirectoryItems(Reiser4PlanObject directory, int maxBody, Reiser4KeyShape keys) {
    var entries = directory.Entries.Select(static (entry, index) => (entry, index)).ToList();
    entries.Sort(static (a, b) => {
      var c = a.entry.Ordering.CompareTo(b.entry.Ordering);
      if (c != 0) return c;
      c = a.entry.ObjectId.CompareTo(b.entry.ObjectId);
      if (c != 0) return c;
      c = a.entry.Offset.CompareTo(b.entry.Offset);
      return c != 0 ? c : a.index.CompareTo(b.index);
    });
    var unitHeaderBytes = keys.UnitHeaderBytes;
    var first = 0;
    while (first < entries.Count) {
      var last = first;
      var bodySize = 2;
      while (last < entries.Count) {
        var entrySize = keys.EntryBytes(entries[last].entry);
        if (bodySize + entrySize > maxBody) break;
        bodySize += entrySize;
        ++last;
      }
      if (last == first) throw new InvalidDataException("Reiser4: a directory unit exceeds node capacity.");
      var body = new byte[bodySize];
      BinaryPrimitives.WriteUInt16LittleEndian(body, checked((ushort)(last - first)));
      var unit = 2 + (last - first) * unitHeaderBytes;
      for (var i = first; i < last; ++i) {
        var entry = entries[i].entry;
        var header = 2 + (i - first) * unitHeaderBytes;
        keys.WriteEntryId(entry, body.AsSpan(header));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(header + keys.EntryIdBytes), checked((ushort)unit));
        keys.WriteTarget(entry.Target, body.AsSpan(unit));
        unit += keys.TargetBytes;
        if (entry.Hashed) {
          entry.Name.CopyTo(body.AsSpan(unit));
          unit += entry.Name.Length + 1;
        }
      }
      var key = entries[first].entry;
      yield return new Item(Reiser4Key.Of(directory.StatKey.ObjectId, Reiser4Key.MinorName, key.Ordering, key.ObjectId, key.Offset),
        Reiser4ItemPlugin.DirEntries, body);
      first = last;
    }
  }

  /// <summary>The body bytes of a directory's entries as its stat data's "bytes" counts them.</summary>
  internal static ulong DirectoryBytes(IEnumerable<Reiser4DirEntry> entries, Reiser4KeyShape keys)
    => (ulong)entries.Sum(e => keys.EntryBytes(e));

  /// <summary>A byte range of <paramref name="payload" />, read lazily.</summary>
  internal static FilePayload Slice(FilePayload payload, long offset, long length) {
    if (offset == 0 && length == payload.Size) return payload;
    if (payload.Data is { } data) return FilePayload.FromBytes(data.AsSpan(checked((int)offset), checked((int)length)).ToArray());
    return FilePayload.FromStream(length, () => SkipTo(payload.Open(), offset));
  }

  private static Stream SkipTo(Stream source, long offset) {
    if (offset <= 0) return source;
    if (source.CanSeek) {
      source.Position = offset;
      return source;
    }
    var buffer = new byte[64 * 1024];
    var remaining = offset;
    while (remaining > 0) {
      var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (n <= 0) break;
      remaining -= n;
    }
    return source;
  }

  private static byte[] PointerBody(ulong block) {
    var body = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(body, block);
    return body;
  }

  private static IEnumerable<List<Item>> Pack(IReadOnlyList<Item> items, int capacity, Reiser4KeyShape keys) {
    var group = new List<Item>();
    var bytes = 0;
    foreach (var item in items) {
      var need = checked(item.Body.Length + keys.ItemHeaderBytes);
      if (need > capacity) throw new InvalidDataException("Reiser4: an indivisible item exceeds node capacity.");
      if (bytes + need > capacity) {
        yield return group;
        group = [];
        bytes = 0;
      }
      group.Add(item);
      bytes += need;
    }
    if (group.Count > 0) yield return group;
  }

  private static byte[] EncodeNode(IReadOnlyList<Item> items, Reiser4Plan plan, int headerBytes, byte level) {
    var blockSize = plan.Layout.BlockSize;
    var node = new byte[blockSize];
    var keys = plan.Keys;
    var itemHeaderBytes = keys.ItemHeaderBytes;
    var at = headerBytes;
    for (var i = 0; i < items.Count; ++i) {
      var item = items[i];
      item.Body.CopyTo(node.AsSpan(at));
      var header = blockSize - (i + 1) * itemHeaderBytes;
      keys.Write(item.Key, node.AsSpan(header));
      BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(header + keys.KeyBytes), checked((ushort)at));
      BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(header + keys.KeyBytes + 4), item.Plugin);
      at += item.Body.Length;
    }
    BinaryPrimitives.WriteUInt16LittleEndian(node, plan.NodePlugin);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2), checked((ushort)items.Count));
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(4), checked((ushort)(blockSize - items.Count * itemHeaderBytes - at)));
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(6), checked((ushort)at));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8), plan.NodePlugin == 1 ? Reiser4Volume.Node41Magic : unchecked((uint)NodeMagic));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(12), plan.MkfsId);
    node[26] = level;
    if (plan.NodePlugin == 1) Reiser4Volume.SealNode41(node);
    return node;
  }

  /// <summary>
  /// Every block-allocator bitmap. Bitmap <c>i</c> covers blocks <c>[i × BlocksPerBitmap, (i+1) ×
  /// BlocksPerBitmap)</c>; the first follows the format superblock (block 18 at 4 KiB) and the rest
  /// sit at the stride boundaries. Blocks below <paramref name="used" />, every bitmap block, the
  /// superblock copies in <paramref name="alsoUsed" /> and everything past the end of the volume are
  /// marked allocated.
  /// </summary>
  internal static List<(ulong Block, byte[] Bytes)> BuildBitmaps(Reiser4Layout layout, ulong blocks, ulong used, IReadOnlyCollection<ulong>? alsoUsed = null) {
    var result = new List<(ulong Block, byte[] Bytes)>();
    HashSet<ulong> extra = alsoUsed is null ? [] : [.. alsoUsed];
    var perBitmap = layout.BlocksPerBitmap;
    var count = (blocks + perBitmap - 1) / perBitmap;
    for (var i = 0UL; i < count; ++i) {
      var block = layout.BitmapBlock(i);
      var buffer = new byte[layout.BlockSize];
      var baseBlock = i * perBitmap;
      for (var bit = 0UL; bit < perBitmap; ++bit) {
        var target = baseBlock + bit;
        if (target >= used && target < blocks && !layout.IsBitmapBlock(target) && !extra.Contains(target)) continue;
        buffer[4 + (int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
      }
      BinaryPrimitives.WriteUInt32LittleEndian(buffer, Adler32.Compute(buffer.AsSpan(4)));
      result.Add((block, buffer));
    }
    return result;
  }
}
