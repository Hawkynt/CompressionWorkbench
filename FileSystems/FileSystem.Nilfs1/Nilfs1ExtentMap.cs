#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Nilfs1;

/// <summary>
/// Walks a NILFS v1 image (as written by <see cref="Nilfs1Writer"/> and mutated by
/// <see cref="Nilfs1InPlaceModifier"/>) and emits its on-disk byte layout: the
/// boot+superblock region, the base directory header + every appended log-segment
/// header/directory as metadata-reserved, and one <see cref="DefragBlockKind.Used"/>
/// extent per <em>currently-live</em> file payload (highest-cno-per-name wins;
/// tombstoned and superseded payloads are deliberately left uncovered so the wipe
/// verb can reclaim/scrub them).
///
/// <para>This live-only extent set is what makes the wipe verb forensically
/// honest on a log-structured volume: <c>Remove</c> only tombstones (snapshot
/// data stays byte-identical), and a subsequent <c>WipeUnusedSpace</c> zero-fills
/// the now-dead payload bytes because they are no longer claimed by any live
/// extent.</para>
///
/// <para>For images we did not write ourselves (no <see cref="Nilfs1Writer.WriterMagic"/>
/// marker) we emit a coarse map: metadata-reserved for the boot+superblock area,
/// free for the rest. NILFS v1's true segment-usage walk is out of scope.</para>
///
/// <para>The image is read through an <see cref="ImageAccessor"/> rather than
/// copied in: the directories are a few kilobytes however many gigabytes of
/// payload they describe.</para>
/// </summary>
public static class Nilfs1ExtentMap {

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 2048) yield break;

    if (image.CanSeek) image.Position = 0;
    using var img = new ImageAccessor(image);
    var length = img.Length;

    // Boot sector + superblock = first 2048 bytes (boot 0..1023, superblock 1024..2047).
    yield return new DefragBlockInfo(0, 2048, DefragBlockKind.MetadataReserved);

    // Detect our writer's directory marker.
    var segStart = (long)Nilfs1Writer.SegmentStart;
    if (segStart + Nilfs1Writer.WriterMagic.Length + 8 > length) yield break;
    var head = img.Read(segStart, Nilfs1Writer.WriterMagic.Length + 8);
    if (!head.AsSpan(0, Nilfs1Writer.WriterMagic.Length).SequenceEqual(Nilfs1Writer.WriterMagic))
      yield break;

    var baseDirSize = BinaryPrimitives.ReadInt64LittleEndian(
      head.AsSpan(Nilfs1Writer.WriterMagic.Length));
    if (baseDirSize < 0 || baseDirSize > int.MaxValue
        || segStart + Nilfs1Writer.WriterMagic.Length + 8 + baseDirSize > length)
      yield break;
    var baseDirStart = segStart + Nilfs1Writer.WriterMagic.Length + 8;
    var basePayloadStart = baseDirStart + baseDirSize;

    // Resolve the winning (offset, size) per live name across the base directory
    // and every appended segment, so only live payload bytes become Used extents.
    var winners = ResolveLiveExtents(img, length, baseDirStart, (int)baseDirSize, basePayloadStart);

    // The base segment header + directory is metadata-reserved.
    yield return new DefragBlockInfo(segStart, basePayloadStart - segStart, DefragBlockKind.MetadataReserved);

    // Each appended segment's header + directory is metadata-reserved; its live
    // payload bytes are emitted as Used (resolved into `winners`); dead payload
    // bytes are simply left uncovered (free → wiped).
    foreach (var meta in winners.MetadataRegions)
      yield return new DefragBlockInfo(meta.Offset, meta.Length, DefragBlockKind.MetadataReserved);

    foreach (var w in winners.Live)
      if (w.Size > 0)
        yield return new DefragBlockInfo(w.Offset, w.Size, DefragBlockKind.Used, w.Name);
  }

  private readonly record struct LiveExtent(string Name, long Offset, long Size, ulong Cno);
  private readonly record struct MetaRegion(long Offset, long Length);

  private sealed class ResolveResult {
    public readonly Dictionary<string, LiveExtent> ByName = new(StringComparer.Ordinal);
    public readonly List<MetaRegion> MetadataRegions = [];
    public IEnumerable<LiveExtent> Live => this.ByName.Values.Where(e => !e.Tombstone());
  }

  private static ResolveResult ResolveLiveExtents(
      ImageAccessor img, long length, long baseDirStart, int baseDirSize, long basePayloadStart) {
    var result = new ResolveResult();

    // Base directory entries are the cno=1 checkpoint.
    var baseDir = img.Read(baseDirStart, baseDirSize);
    var cursor = 0;
    var basePayloadEnd = basePayloadStart;
    while (cursor + 4 <= baseDir.Length) {
      var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(baseDir.AsSpan(cursor));
      cursor += 4;
      if (nameLen <= 0 || cursor + nameLen + 16 > baseDir.Length) break;
      var name = System.Text.Encoding.UTF8.GetString(baseDir, cursor, nameLen);
      cursor += nameLen;
      var off = BinaryPrimitives.ReadInt64LittleEndian(baseDir.AsSpan(cursor));
      cursor += 8;
      var size = BinaryPrimitives.ReadInt64LittleEndian(baseDir.AsSpan(cursor));
      cursor += 8;
      if (size < 0 || off < 0 || basePayloadStart + off + size > length) break;
      result.ByName[name] = new LiveExtent(name, basePayloadStart + off, size, 1ul);
      basePayloadEnd = Math.Max(basePayloadEnd, basePayloadStart + off + size);
    }

    // Appended segments supersede with higher cno; tombstones mark dead.
    // They can only live past the base payload, which is where the scan starts.
    var magic = Nilfs1Writer.SegmentMagic;
    var p = basePayloadEnd;
    while (p + magic.Length + 24 <= length) {
      if (!img.Read(p, magic.Length).AsSpan().SequenceEqual(magic)) { ++p; continue; }
      var hdr = img.Read(p + magic.Length, 24);
      var cno = BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(0));
      var entryCount = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(8));
      var dirSize = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(16));
      var dStart = p + magic.Length + 24;
      if (dirSize < 0 || dirSize > int.MaxValue || dStart + dirSize > length
          || entryCount < 0 || entryCount > length) {
        ++p; continue;
      }
      var dir = img.Read(dStart, (int)dirSize);
      var payloadStart = dStart + dirSize;
      var c = 0;
      var consumedPayload = 0L;
      var parsedOk = true;
      var pending = new List<LiveExtent>();
      var pendingTomb = new List<string>();
      for (var i = 0L; i < entryCount; ++i) {
        if (c + 4 > dir.Length) { parsedOk = false; break; }
        var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(c));
        c += 4;
        if (nameLen <= 0 || c + nameLen + 1 + 16 > dir.Length) { parsedOk = false; break; }
        var name = System.Text.Encoding.UTF8.GetString(dir, c, nameLen);
        c += nameLen;
        var tombstone = dir[c] != 0;
        c += 1;
        var off = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(c));
        c += 8;
        var size = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(c));
        c += 8;
        if (size < 0 || off < 0) { parsedOk = false; break; }
        if (tombstone || size == 0) {
          pendingTomb.Add(name);
        } else {
          if (payloadStart + off + size > length) { parsedOk = false; break; }
          pending.Add(new LiveExtent(name, payloadStart + off, size, cno));
          consumedPayload = Math.Max(consumedPayload, off + size);
        }
      }
      if (!parsedOk) { ++p; continue; }

      // Header + directory of this segment is metadata-reserved.
      result.MetadataRegions.Add(new MetaRegion(p, payloadStart - p));
      foreach (var e in pending)
        if (!result.ByName.TryGetValue(e.Name, out var prev) || cno >= prev.Cno)
          result.ByName[e.Name] = e;
      foreach (var name in pendingTomb)
        if (!result.ByName.TryGetValue(name, out var prev) || cno >= prev.Cno)
          result.ByName[name] = new LiveExtent(name, -1, 0, cno); // tombstone marker

      p = payloadStart + consumedPayload;
    }

    return result;
  }

  private static bool Tombstone(this LiveExtent e) => e.Offset < 0;
}
