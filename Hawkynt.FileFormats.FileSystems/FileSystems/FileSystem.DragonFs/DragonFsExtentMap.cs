#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.DragonFs;

/// <summary>
/// Reports where a DragonFS volume's bytes are: each file as its directory
/// record followed by its data, and whatever nothing links to as free.
/// </summary>
/// <remarks>
/// A file here has no address of its own. Its bytes begin immediately after the
/// thirty-two byte record that names it, and the record is reached by a pointer
/// in the record before it — or, for the first of a directory, by the pointer
/// in the parent. So the unit that can be moved is the pair, and what has to be
/// rewritten afterwards is whoever pointed at it.
/// </remarks>
public static class DragonFsExtentMap {

  /// <summary>Bytes one directory record occupies.</summary>
  public const int RecordSize = 32;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new DragonFsReader(image);
      if (!reader.ValidRoot) return [];

      var sizeOf = new Dictionary<long, (string Name, long Size)>();
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory || entry.Size <= 0) continue;
        sizeOf[(long)entry.DataOffset - RecordSize] = (entry.Name, entry.Size);
      }

      var claimed = new List<(long Start, long End)>();
      var files = new List<DefragBlockInfo>();

      // Every record the chain passes through has to stay, whether or not a
      // live file hangs off it: removing a file leaves its record in place with
      // the link to everything after it, so zeroing that record cuts the chain
      // and every later file goes with it.
      foreach (var at in ChainRecords(image, reader.RootOffset, sizeOf.Keys)) {
        if (sizeOf.TryGetValue(at, out var file)) {
          var length = RecordSize + file.Size;
          if (at + length > image.Length) continue;
          files.Add(new DefragBlockInfo(at, length, DefragBlockKind.Used, file.Name));
          claimed.Add((at, at + length));
          continue;
        }
        if (at + RecordSize > image.Length) continue;
        files.Add(new DefragBlockInfo(at, RecordSize, DefragBlockKind.MetadataReserved));
        claimed.Add((at, at + RecordSize));
      }
      claimed.Sort((a, b) => a.Start.CompareTo(b.Start));

      // Everything ahead of the first record is fixed: the root is found at a
      // known offset, not by following anything, and the writer lays the boot
      // area out around it. Reading that boundary off the records rather than
      // computing it from the root offset matters — the first record does not
      // begin a fixed distance after the root, and assuming it did left the
      // first file unclaimed, which a wipe then zeroed.
      var head = claimed.Count > 0
        ? Math.Min(claimed[0].Start, reader.RootOffset)
        : image.Length;
      if (head > 0)
        result.Add(new DefragBlockInfo(0, head, DefragBlockKind.MetadataReserved, "boot area and root record"));
      result.AddRange(files);

      var cursor = head;
      foreach (var (start, end) in claimed) {
        if (start > cursor)
          result.Add(new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free));
        cursor = Math.Max(cursor, end);
      }
      if (cursor < image.Length)
        result.Add(new DefragBlockInfo(cursor, image.Length - cursor, DefragBlockKind.Free));
    } catch {
      // A volume we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <summary>Every record the directory chain passes through, root first.</summary>
  private static IEnumerable<long> ChainRecords(Stream image, long rootOffset, IEnumerable<long> liveRecords) {
    var record = new byte[RecordSize];
    var seen = new HashSet<long>();
    var pending = new Queue<long>();
    // Where the root sits is read off the image, not assumed: an image carrying
    // the optional tag puts it eight bytes further on, and walking from the
    // wrong place reached none of the records — so a removed file's record,
    // which the chain still runs through to reach everything after it, looked
    // like free space and a wipe cut the chain.
    pending.Enqueue(rootOffset);
    // Seed with the records the reader found too. Where the chain begins is not
    // a fixed distance from the root — the writer places it — so following only
    // from the root can reach nothing at all.
    foreach (var at in liveRecords) pending.Enqueue(at);

    while (pending.Count > 0) {
      var at = pending.Dequeue();
      while (at > 0 && at + RecordSize <= image.Length && seen.Add(at)) {
        image.Position = at;
        image.ReadExactly(record);
        var next = (long)BinaryPrimitives.ReadUInt32BigEndian(record);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4));
        if ((flags & 0x0002) != 0) break;                    // end marker
        var child = (long)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(28));

        // The record at the root offset is the first entry, not a container
        // holding them: skipping it left the first file unclaimed.
        yield return at;
        if ((flags & 0x0001) != 0 && child != 0) pending.Enqueue(child);

        if (next == 0) break;
        at = next;
      }
    }
  }
}
