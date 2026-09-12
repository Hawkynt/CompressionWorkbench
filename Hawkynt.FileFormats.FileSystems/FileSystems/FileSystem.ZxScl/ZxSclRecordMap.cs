#pragma warning disable CS1591
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.ZxScl;

/// <summary>
/// Describes an SCL container one payload at a time, together with the two
/// things a payload may not be moved onto: the directory in front of it and the
/// checksum behind it.
/// </summary>
/// <remarks>
/// <para>Nothing in an SCL says where a file's bytes are. The directory records
/// a length in sectors and nothing else, and the reader finds each payload by
/// adding the previous one's length to a cursor that starts just past the
/// directory. A payload's position is therefore its position in the directory,
/// and the two orders are the same order by construction.</para>
///
/// <para>So a run is movable only as far as the walk still reaches it, which
/// means the payloads have to stay packed against the directory and in the
/// order the directory lists them. What a pass over one of these is for is
/// telling that this is already the case — the answer on a container we wrote
/// ourselves, where removing a file physically closes the gap it left — rather
/// than writing the whole container out again to find out.</para>
/// </remarks>
public static class ZxSclRecordMap {

  /// <summary>Magic plus the one byte counting the directory entries.</summary>
  internal const int CountOffset = 8;

  /// <summary>The trailing checksum's width.</summary>
  internal const int ChecksumSize = 4;

  /// <summary>Where the payloads begin: past the magic, the count and the directory.</summary>
  internal static long PayloadStart(int entryCount)
    => CountOffset + 1 + (long)entryCount * ZxSclReader.HeaderSize;

  /// <summary>The layout a pass plans against.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var data = new ImageAccessor(image, leaveOpen: true);
    if (data.Length < ZxSclReader.Magic.Length + 1 + ChecksumSize) yield break;
    if (!data.Read(0, ZxSclReader.Magic.Length).AsSpan().SequenceEqual(ZxSclReader.Magic)) yield break;

    var count = data.Read(CountOffset, 1)[0];
    var payloadStart = PayloadStart(count);
    var checksumAt = data.Length - ChecksumSize;
    if (payloadStart > checksumAt) yield break;

    yield return new DefragBlockInfo(
      0, payloadStart, DefragBlockKind.MetadataReserved, "SINCLAIR magic and directory");

    var cursor = payloadStart;
    for (var i = 0; i < count; ++i) {
      var header = data.Read(payloadStart - (long)(count - i) * ZxSclReader.HeaderSize, ZxSclReader.HeaderSize);
      var length = (long)header[13] * ZxSclReader.SectorSize;
      if (length <= 0 || cursor + length > checksumAt) yield break;

      yield return new DefragBlockInfo(cursor, length, DefragBlockKind.Used, NameOf(header, i));
      cursor += length;
    }

    // Whatever the directory does not account for. A container we wrote has
    // none; one that arrived from somewhere else may.
    if (cursor < checksumAt)
      yield return new DefragBlockInfo(cursor, checksumAt - cursor, DefragBlockKind.Free, null);

    yield return new DefragBlockInfo(
      checksumAt, ChecksumSize, DefragBlockKind.MetadataReserved, "Checksum");
  }

  /// <summary>
  /// What to call a payload. The name is only a label here — a move is found
  /// again by where the run was, because two entries may well share a name.
  /// </summary>
  private static string NameOf(byte[] header, int index) {
    var end = 8;
    while (end > 0 && header[end - 1] is 0x20 or 0x00) --end;
    var name = System.Text.Encoding.ASCII.GetString(header, 0, end);
    return name.Length == 0 ? $"#{index}" : $"{name}.{(char)header[8]}";
  }
}
