#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.UefiFv;

internal static class UefiFvMaintenance {
  internal static void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException($"UEFI FV defrag supports only {DefragMode.ConsolidateAtStart}.");

    var (image, volume) = OpenWritable(archive);
    options.CancellationToken.ThrowIfCancellationRequested();
    options.OnProgress?.Invoke(new DefragProgressEvent("scanning", 0, -1, -1, image.Length, null, "Scanning UEFI firmware volume"));

    var output = image.ToArray();
    output.AsSpan(volume.DataStart, volume.End - volume.DataStart).Fill(volume.EraseByte);
    var live = UefiFvParser.LiveSlots(volume).ToList();
    var anchored = live.Where(IsAnchored).ToList();
    foreach (var slot in anchored)
      image.AsSpan(slot.Offset, slot.Size).CopyTo(output.AsSpan(slot.Offset, slot.Size));

    var movable = live.Where(slot => !IsAnchored(slot)).ToList();
    for (var i = 0; i < movable.Count; i++) {
      options.CancellationToken.ThrowIfCancellationRequested();
      var slot = movable[i];
      var footprint = UefiFvConstants.Align8(slot.Size);
      var destination = FindRun(output, volume, footprint);
      if (destination < 0)
        throw new InvalidDataException($"UEFI FV cannot place {slot.Name:D} while preserving fixed/aligned files.");
      image.AsSpan(slot.Offset, slot.Size).CopyTo(output.AsSpan(destination, slot.Size));
      options.OnProgress?.Invoke(new DefragProgressEvent("writing", (i + 1d) / Math.Max(1, movable.Count),
        slot.Offset, destination, image.Length, null, UefiFvWriter.EntryName(slot.Name, slot.Type)));
    }

    var packed = UefiFvParser.Parse(output, volume.Start);
    var usedEnd = UefiFvParser.LiveSlots(packed).Select(slot => slot.DataEnd).DefaultIfEmpty(packed.DataStart).Max();
    UefiFvParser.WriteUsedSize(output, packed, usedEnd);
    VerifyPayloads(image, output, volume.Start);
    options.CancellationToken.ThrowIfCancellationRequested();
    Commit(archive, output);
    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, output.Length, null, "UEFI FV defragmented"));
  }

  internal static void Purge(Stream archive) {
    var (image, volume) = OpenWritable(archive);
    image.AsSpan(volume.DataStart, volume.End - volume.DataStart).Fill(volume.EraseByte);
    UefiFvParser.WriteUsedSize(image, volume, volume.DataStart);
    Commit(archive, image);
  }

  internal static (byte[] Image, UefiFvLayout Volume) Read(Stream archive, bool writable) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanSeek || writable && !archive.CanWrite)
      throw new ArgumentException(writable ? "UEFI FV maintenance requires a readable/writable/seekable stream." : "UEFI FV layout requires a readable/seekable stream.", nameof(archive));
    if (archive.Length > int.MaxValue)
      throw new NotSupportedException("The in-memory UEFI FV implementation supports images up to 2 GiB.");
    archive.Position = 0;
    var image = new byte[checked((int)archive.Length)];
    archive.ReadExactly(image);
    var start = UefiFvReader.FindFirst(image) ?? throw new InvalidDataException("UEFI FV header was not found.");
    return (image, UefiFvParser.Parse(image, start));
  }

  internal static void EnsureMutable(UefiFvLayout volume) {
    if (volume.FileSystemGuid != UefiFvConstants.Ffs2Guid && volume.FileSystemGuid != UefiFvConstants.Ffs3Guid)
      throw new NotSupportedException($"UEFI FV uses unsupported file-system GUID {volume.FileSystemGuid:D}.");
    if (volume.IsSigned)
      throw new NotSupportedException("Signed UEFI firmware volumes cannot be modified without re-signing the complete volume.");
  }

  internal static void Commit(Stream archive, byte[] image) {
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  private static (byte[] Image, UefiFvLayout Volume) OpenWritable(Stream archive) {
    var result = Read(archive, writable: true);
    EnsureMutable(result.Volume);
    return result;
  }

  private static bool IsAnchored(UefiFvSlot slot)
    => slot.IsPad || slot.IsFixed || slot.Name == UefiFvConstants.VolumeTopGuid || slot.DataAlignment > 1;

  private static int FindRun(byte[] image, UefiFvLayout volume, int footprint) {
    for (var p = volume.DataStart; p + footprint <= volume.End; p += UefiFvWriter.Alignment)
      if (UefiFvParser.IsErased(image.AsSpan(p, footprint), volume.EraseByte)) return p;
    return -1;
  }

  private static void VerifyPayloads(byte[] before, byte[] after, int start) {
    var expected = UefiFvReader.Read(before, start).Files.Where(f => f.Type != 0xF0).ToList();
    var actual = UefiFvReader.Read(after, start).Files.Where(f => f.Type != 0xF0).ToList();
    if (expected.Count != actual.Count) throw new InvalidDataException("UEFI FV defrag changed the live-file count.");
    foreach (var file in expected) {
      var index = actual.FindIndex(candidate => candidate.Name == file.Name && candidate.Type == file.Type
        && candidate.Contents.AsSpan().SequenceEqual(file.Contents));
      if (index < 0) throw new InvalidDataException($"UEFI FV defrag changed payload {file.Name:D}.");
      actual.RemoveAt(index);
    }
  }
}
