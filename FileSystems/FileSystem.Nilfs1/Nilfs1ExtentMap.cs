#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Nilfs1;

/// <summary>
/// Walks a NILFS v1 image (as written by <see cref="Nilfs1Writer"/>) and emits
/// its on-disk byte layout: the 1024 KB metadata-reserved boot+superblock region,
/// the segment header + directory index region, every file payload as a separate
/// <see cref="DefragBlockKind.Used"/> extent, and the trailing free area.
///
/// <para>For images we did not write ourselves (no <see cref="Nilfs1Writer.WriterMagic"/>
/// marker) we still emit a coarse map: metadata-reserved for the boot+superblock
/// area, free for the rest of the image. NILFS v1's true segment usage walk is
/// out of scope (multi-week effort).</para>
/// </summary>
public static class Nilfs1ExtentMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 2048) yield break;

    // Boot sector + superblock = first 2048 bytes (boot 0..1023, superblock 1024..2047).
    yield return new DefragBlockInfo(0, 2048, DefragBlockKind.MetadataReserved);

    // Detect our writer's directory marker.
    image.Position = Nilfs1Writer.SegmentStart;
    var marker = new byte[Nilfs1Writer.WriterMagic.Length + 8];
    var read = image.Read(marker, 0, marker.Length);
    if (read < marker.Length ||
        !marker.AsSpan(0, Nilfs1Writer.WriterMagic.Length).SequenceEqual(Nilfs1Writer.WriterMagic)) {
      yield break;
    }
    var dirSize = BinaryPrimitives.ReadInt64LittleEndian(marker.AsSpan(Nilfs1Writer.WriterMagic.Length));
    if (dirSize < 0 || Nilfs1Writer.SegmentStart + Nilfs1Writer.WriterMagic.Length + 8 + dirSize > image.Length)
      yield break;
    var dirStart = Nilfs1Writer.SegmentStart + Nilfs1Writer.WriterMagic.Length + 8;
    var payloadStart = dirStart + (int)dirSize;

    // The segment header + directory becomes metadata-reserved.
    yield return new DefragBlockInfo(
      Nilfs1Writer.SegmentStart,
      payloadStart - Nilfs1Writer.SegmentStart,
      DefragBlockKind.MetadataReserved);

    // Parse the directory and emit one Used extent per file payload.
    image.Position = dirStart;
    var dir = new byte[(int)dirSize];
    if (image.Read(dir, 0, dir.Length) < dir.Length) yield break;

    var cursor = 0;
    while (cursor + 4 <= dir.Length) {
      var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(cursor));
      cursor += 4;
      if (nameLen <= 0 || cursor + nameLen + 16 > dir.Length) break;
      var name = System.Text.Encoding.UTF8.GetString(dir, cursor, nameLen);
      cursor += nameLen;
      var off = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(cursor));
      cursor += 8;
      var size = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(cursor));
      cursor += 8;
      if (size < 0 || off < 0 || payloadStart + off + size > image.Length) break;
      if (size > 0)
        yield return new DefragBlockInfo(payloadStart + off, size, DefragBlockKind.Used, name);
    }
  }
}
