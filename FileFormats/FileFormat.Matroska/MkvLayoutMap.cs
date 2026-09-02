#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Matroska;

/// <summary>
/// Walks the top-level and first-level EBML elements of a Matroska/WebM file and
/// emits <see cref="DefragBlockInfo"/> tiles. SeekHead/Info/Tracks/Cues are
/// classified as MetadataReserved; each Cluster is classified as Used.
/// </summary>
public static class MkvLayoutMap {

  // EBML element IDs (with leading-bit marker)
  private const ulong Id_EbmlHeader = 0x1A45DFA3;
  private const ulong Id_Segment = 0x18538067;
  private const ulong Id_SeekHead = 0x114D9B74;
  private const ulong Id_Info = 0x1549A966;
  private const ulong Id_Tracks = 0x1654AE6B;
  private const ulong Id_Cues = 0x1C53BB6B;
  private const ulong Id_Cluster = 0x1F43B675;
  private const ulong Id_Attachments = 0x1941A469;
  private const ulong Id_Chapters = 0x1043A770;
  private const ulong Id_Tags = 0x1254C367;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 4)
      yield break;

    using var ms = new MemoryStream();
    file.Position = 0;
    file.CopyTo(ms);
    var data = ms.ToArray();

    var ebml = new EbmlReader(data);
    var pos = 0L;

    // Read the EBML header element
    var headerEl = ebml.Read(ref pos);
    if (headerEl == null)
      yield break;

    var h = headerEl.Value;
    var headerStart = h.BodyOffset - EstimateHeaderLen(data, 0, h);
    var headerTotalLen = h.BodyOffset - headerStart + h.BodyLength;
    yield return new DefragBlockInfo(headerStart, headerTotalLen,
      DefragBlockKind.MetadataReserved, "EBML header", DefragBlockClass.Hot);

    // Read the Segment element
    var segEl = ebml.Read(ref pos);
    if (segEl == null)
      yield break;

    var seg = segEl.Value;
    if (seg.Id != Id_Segment) {
      // Not a Segment — emit as unknown
      var segStart2 = seg.BodyOffset - EstimateHeaderLen(data, headerStart + headerTotalLen, seg);
      yield return new DefragBlockInfo(segStart2, seg.BodyOffset - segStart2 + seg.BodyLength,
        DefragBlockKind.Used, $"Element 0x{seg.Id:X}", DefragBlockClass.Normal);
      yield break;
    }

    // Walk first-level children of Segment
    foreach (var child in ebml.Children(seg)) {
      var elStart = child.BodyOffset - EstimateHeaderLen(data, child.BodyOffset, child);
      var elTotalLen = child.BodyOffset - elStart + child.BodyLength;

      var (kind, name, cls) = ClassifySegmentChild(child.Id);
      yield return new DefragBlockInfo(elStart, elTotalLen, kind, name, cls);
    }
  }

  private static (DefragBlockKind Kind, string Name, DefragBlockClass Class) ClassifySegmentChild(ulong id) => id switch {
    Id_SeekHead => (DefragBlockKind.MetadataReserved, "SeekHead", DefragBlockClass.Hot),
    Id_Info => (DefragBlockKind.MetadataReserved, "Info", DefragBlockClass.Hot),
    Id_Tracks => (DefragBlockKind.MetadataReserved, "Tracks", DefragBlockClass.Hot),
    Id_Cues => (DefragBlockKind.MetadataReserved, "Cues (index)", DefragBlockClass.Normal),
    Id_Cluster => (DefragBlockKind.Used, "Cluster", DefragBlockClass.Normal),
    Id_Attachments => (DefragBlockKind.Used, "Attachments", DefragBlockClass.Cold),
    Id_Chapters => (DefragBlockKind.MetadataReserved, "Chapters", DefragBlockClass.Normal),
    Id_Tags => (DefragBlockKind.MetadataReserved, "Tags", DefragBlockClass.Cold),
    _ => (DefragBlockKind.Used, $"Element 0x{id:X}", DefragBlockClass.Normal),
  };

  /// <summary>
  /// Estimates the EBML element header length (ID + size fields) by computing
  /// the difference between where the body starts and where we expect the element
  /// to have begun. Since EbmlReader only gives us BodyOffset, we approximate
  /// by subtracting from a known earlier position.
  /// </summary>
  private static long EstimateHeaderLen(byte[] data, long approximateStart, EbmlReader.Element el) {
    // Walk from approximateStart to find the element's ID start
    // The header = ID bytes + size bytes. We know BodyOffset = start + idLen + sizeLen.
    // For the first element, approximateStart = 0.
    // For children, the EbmlReader reads sequentially so we can just use BodyOffset - start.
    var idStart = el.BodyOffset;

    // Walk backwards to find the element header start
    // ID length: 1-4 bytes (first byte determines), Size length: 1-8 bytes
    // We'll compute from the raw data if possible
    if (idStart >= 2 && idStart <= data.Length) {
      // Try estimating: scan backward from BodyOffset to find ID byte with leading bit
      for (var tryLen = 2; tryLen <= 12 && idStart - tryLen >= 0; ++tryLen) {
        var candidateStart = idStart - tryLen;
        var firstByte = data[candidateStart];
        var idLen = VintLength(firstByte);
        if (candidateStart + idLen < data.Length) {
          var sizePos = candidateStart + idLen;
          if (sizePos < data.Length) {
            var sizeLen = VintLength(data[sizePos]);
            if (candidateStart + idLen + sizeLen == idStart) {
              return tryLen;
            }
          }
        }
      }
    }
    // Fallback: assume 8 bytes (4 ID + 4 size) which is typical for top-level
    return Math.Min(8, idStart - approximateStart);
  }

  private static int VintLength(byte first) {
    for (var i = 0; i < 8; ++i)
      if ((first & (0x80 >> i)) != 0) return i + 1;
    return 8;
  }
}
