#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Dmg;

/// <summary>
/// Builds the physical UDIF container map used by maintenance operations.
/// Only the CompressionWorkbench raw profile exposes unreferenced data-fork
/// gaps as free space; foreign images conservatively classify every unknown
/// byte as metadata so wipe can never invalidate checksums, signatures, or
/// private resource-fork data that the reader does not understand.
/// </summary>
internal static class DmgLayoutMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanSeek)
      throw new ArgumentException("DMG layout requires a readable, seekable stream.", nameof(archive));

    archive.Position = 0;
    using var reader = new DmgReader(archive, leaveOpen: true);
    if (reader.FileLength <= 0)
      yield break;

    List<Claim> claims = [];
    var blockMapIsTrustworthy = true;
    try {
      foreach (var partition in reader.Partitions) {
        var table = DmgReader.ParseMish(partition.Mish);
        if (table == null) continue;

        foreach (var block in table.Blocks) {
          if (block.Type is DmgReader.BlockTypeZeroFill or DmgReader.BlockTypeIgnore or
              DmgReader.BlockTypeComment or DmgReader.BlockTypeTerminator || block.CompressedLength == 0)
            continue;

          var (offset, length) = reader.GetStoredRange(block);
          if (length > 0)
            claims.Add(new Claim(offset, length, DefragBlockKind.Used, partition.Name));
        }
      }
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException) {
      blockMapIsTrustworthy = false;
    }

    // Fail closed: a map that cannot prove the block layout must not expose
    // any part of the image as free to the generic wiper.
    if (!blockMapIsTrustworthy) {
      yield return new DefragBlockInfo(0, reader.FileLength, DefragBlockKind.MetadataReserved,
        "UDIF container (unverified block map)");
      yield break;
    }

    claims.Add(new Claim(reader.XmlOffset, reader.XmlLength, DefragBlockKind.MetadataReserved, "UDIF XML plist"));
    claims.Add(new Claim(reader.FileLength - DmgWriter.KolySize, DmgWriter.KolySize,
      DefragBlockKind.MetadataReserved, "UDIF koly trailer"));

    claims.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
    var cursor = 0L;
    foreach (var claim in claims) {
      var start = Math.Clamp(claim.Offset, 0, reader.FileLength);
      var end = Math.Clamp(checked(claim.Offset + claim.Length), 0, reader.FileLength);
      if (end <= start) continue;

      if (start > cursor)
        yield return Gap(reader, cursor, start - cursor);

      if (end <= cursor) continue;
      var visibleStart = Math.Max(start, cursor);
      yield return new DefragBlockInfo(visibleStart, end - visibleStart, claim.Kind, claim.Name);
      cursor = end;
    }

    if (cursor < reader.FileLength)
      yield return Gap(reader, cursor, reader.FileLength - cursor);
  }

  private static DefragBlockInfo Gap(DmgReader reader, long offset, long length) {
    var dataForkEnd = checked(reader.DataForkOffset + reader.DataForkLength);
    var entirelyInsideDataFork = offset >= reader.DataForkOffset && checked(offset + length) <= dataForkEnd;
    var kind = reader.IsWorkbenchRawProfile && entirelyInsideDataFork
      ? DefragBlockKind.Free
      : DefragBlockKind.MetadataReserved;
    return new DefragBlockInfo(offset, length, kind,
      kind == DefragBlockKind.Free ? "unreferenced UDIF data-fork bytes" : "UDIF reserved/unknown bytes");
  }

  private sealed record Claim(long Offset, long Length, DefragBlockKind Kind, string Name);
}
