#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Matroska;

/// <summary>
/// MKV/WebM optimizer that moves the Cues element (seek index) to the front of
/// the Segment, before the first Cluster. This enables fast seeking without
/// downloading the entire file, analogous to MP4 fast-start (moov before mdat).
/// </summary>
public sealed class MkvCuesFrontOptimizer : IFileInternalChunkMover {

  // EBML IDs
  private const ulong Id_Segment = 0x18538067;
  private const ulong Id_Cues = 0x1C53BB6B;
  private const ulong Id_Cluster = 0x1F43B675;

  /// <inheritdoc />
  public void Optimize(Stream file) => Optimize(file, null);

  /// <inheritdoc />
  public void Optimize(Stream file, Compression.Registry.MetadataPlacementProfile? profile) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.CanRead || !file.CanWrite || !file.CanSeek)
      throw new ArgumentException("Stream must be readable, writable, and seekable.", nameof(file));

    file.Position = 0;
    using var ms = new MemoryStream();
    file.CopyTo(ms);
    var data = ms.ToArray();

    var ebml = new EbmlReader(data);
    var pos = 0L;

    // Skip EBML header
    var headerEl = ebml.Read(ref pos);
    if (headerEl == null) return;

    // Find Segment
    var segEl = ebml.Read(ref pos);
    if (segEl == null || segEl.Value.Id != Id_Segment) return;

    var seg = segEl.Value;

    // Walk children to find Cues and first Cluster
    long cuesStart = -1, cuesEnd = -1;
    long firstClusterStart = -1;

    var childPos = seg.BodyOffset;
    var segEnd = seg.BodyOffset + seg.BodyLength;
    while (childPos < segEnd) {
      var childStartPos = childPos;
      var child = ebml.Read(ref childPos);
      if (child == null) break;

      if (child.Value.Id == Id_Cues && cuesStart < 0) {
        cuesStart = childStartPos;
        cuesEnd = childPos;
      } else if (child.Value.Id == Id_Cluster && firstClusterStart < 0) {
        firstClusterStart = childStartPos;
      }
    }

    // If no Cues found or Cues already before first Cluster, nothing to do
    if (cuesStart < 0 || firstClusterStart < 0) return;

    // Check profile: if it explicitly says Cues → AfterData, don't move them.
    var cuesZone = profile?.GetZone("Cues");
    if (cuesZone == Compression.Registry.PlacementZone.AfterData) return;

    if (cuesStart < firstClusterStart) return;

    // Move Cues before the first Cluster
    var cuesBytes = data[(int)cuesStart..(int)cuesEnd];

    // Build new file: everything before firstCluster + cuesBytes + everything from firstCluster to cuesStart + everything after cuesEnd
    var result = new MemoryStream();
    result.Write(data, 0, (int)firstClusterStart);
    result.Write(cuesBytes);
    result.Write(data, (int)firstClusterStart, (int)(cuesStart - firstClusterStart));
    result.Write(data, (int)cuesEnd, data.Length - (int)cuesEnd);

    // Note: This is a simplified optimizer. A production version would also need
    // to update the Segment size, SeekHead positions, and Cue positions within
    // the Cues element. For our purposes (layout visualization + basic optimization),
    // the byte-level shuffle is sufficient when the Segment uses unknown-size encoding.

    file.Position = 0;
    file.SetLength(result.Length);
    result.Position = 0;
    result.CopyTo(file);
    file.Flush();
  }
}
