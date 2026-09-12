#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.UefiFv;

internal static class UefiFvLayoutMap {
  internal static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    var (image, volume) = UefiFvMaintenance.Read(archive, writable: false);
    return Build(image.Length, volume, includeDeletedAsFree: true);
  }

  internal static long Wipe(Stream archive, bool wipeDeletedEntries) {
    var (image, volume) = UefiFvMaintenance.Read(archive, writable: true);
    UefiFvMaintenance.EnsureMutable(volume);
    long written = 0;
    foreach (var extent in Build(image.Length, volume, wipeDeletedEntries).Where(e => e.Kind == DefragBlockKind.Free)) {
      image.AsSpan(checked((int)extent.Offset), checked((int)extent.Length)).Fill(volume.EraseByte);
      written += extent.Length;
    }

    var reparsed = UefiFvParser.Parse(image, volume.Start);
    var usedEnd = UefiFvParser.LiveSlots(reparsed).Select(slot => slot.DataEnd).DefaultIfEmpty(reparsed.DataStart).Max();
    UefiFvParser.WriteUsedSize(image, reparsed, usedEnd);
    UefiFvMaintenance.Commit(archive, image);
    return written;
  }

  private static List<DefragBlockInfo> Build(int imageLength, UefiFvLayout volume, bool includeDeletedAsFree) {
    var result = new List<DefragBlockInfo>();
    if (volume.DataStart > 0)
      result.Add(new DefragBlockInfo(0, volume.DataStart, DefragBlockKind.MetadataReserved, "FV header / prefix"));

    var cursor = volume.DataStart;
    foreach (var slot in volume.Slots.OrderBy(slot => slot.Offset)) {
      if (cursor < slot.Offset)
        result.Add(new DefragBlockInfo(cursor, slot.Offset - cursor, DefragBlockKind.Free));

      DefragBlockKind kind;
      string? name = null;
      if (UefiFvParser.IsLive(volume, slot)) {
        kind = slot.IsPad ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used;
        name = slot.IsPad ? "FFS pad" : UefiFvWriter.EntryName(slot.Name, slot.Type);
      } else if (includeDeletedAsFree && UefiFvParser.IsDiscardable(volume, slot)) {
        kind = DefragBlockKind.Free;
      } else {
        kind = DefragBlockKind.MetadataReserved;
        name = "Incomplete FFS record";
      }
      result.Add(new DefragBlockInfo(slot.Offset, slot.Footprint, kind, name));
      cursor = slot.End;
    }

    if (cursor < volume.End)
      result.Add(new DefragBlockInfo(cursor, volume.End - cursor, DefragBlockKind.Free));
    if (volume.End < imageLength)
      result.Add(new DefragBlockInfo(volume.End, imageLength - volume.End, DefragBlockKind.MetadataReserved, "FV suffix"));
    return result;
  }
}
