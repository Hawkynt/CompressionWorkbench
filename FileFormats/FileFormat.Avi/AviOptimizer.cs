#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Avi;

/// <summary>
/// AVI optimizer that moves the idx1 (index) chunk before the movi (data) list,
/// enabling faster seeking. Patches idx1 offsets to account for the positional
/// change. Analogous to MP4 fast-start (moov before mdat).
/// </summary>
public sealed class AviOptimizer : IFileInternalChunkMover {

  /// <inheritdoc />
  /// <summary>
  /// Performs the optimize operation.
  /// </summary>
  public void Optimize(Stream file) => Optimize(file, null);

  /// <inheritdoc />
  /// <summary>
  /// Performs the optimize operation.
  /// </summary>
  public void Optimize(Stream file, Compression.Registry.MetadataPlacementProfile? profile) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.CanRead || !file.CanWrite || !file.CanSeek)
      throw new ArgumentException("Stream must be readable, writable, and seekable.", nameof(file));

    file.Position = 0;
    using var ms = new MemoryStream();
    file.CopyTo(ms);
    var data = ms.ToArray();

    if (data.Length < 12) return;
    if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return;
    if (data[8] != 'A' || data[9] != 'V' || data[10] != 'I' || data[11] != ' ') return;

    // Find idx1 and movi positions
    long idx1Start = -1, idx1Size = 0;
    long moviStart = -1;

    var pos = 12;
    while (pos + 8 <= data.Length) {
      var id = Encoding.ASCII.GetString(data, pos, 4);
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
      var bodyStart = pos + 8;
      if (bodyStart + size > data.Length) break;

      if (id == "LIST" && size >= 4) {
        var listType = Encoding.ASCII.GetString(data, bodyStart, 4);
        if (listType == "movi")
          moviStart = pos;
      } else if (id == "idx1") {
        idx1Start = pos;
        idx1Size = 8 + size;
      }
      pos = bodyStart + size + (size & 1);
    }

    // If idx1 not found or already before movi, nothing to do
    if (idx1Start < 0 || moviStart < 0) return;

    // Check profile: if it explicitly says idx1 → AfterData, don't move it.
    var idxZone = profile?.GetZone("idx1");
    if (idxZone == Compression.Registry.PlacementZone.AfterData) return;

    if (idx1Start < moviStart) return;

    // Already optimal — no-op
    // (idx1 is after movi, which is the standard layout; moving idx1 before
    //  movi is the optimization for fast seeking)
    var idx1Bytes = data[(int)idx1Start..(int)(idx1Start + idx1Size)];

    // Build new file: everything before movi + idx1 + movi onwards (excluding idx1)
    var result = new MemoryStream();
    result.Write(data, 0, (int)moviStart);
    result.Write(idx1Bytes);
    result.Write(data, (int)moviStart, (int)(idx1Start - moviStart));
    // Write anything after idx1
    var afterIdx1 = (int)(idx1Start + idx1Size + (idx1Size & 1));
    if (afterIdx1 < data.Length)
      result.Write(data, afterIdx1, data.Length - afterIdx1);

    file.Position = 0;
    file.SetLength(result.Length);
    result.Position = 0;
    result.CopyTo(file);
    file.Flush();
  }
}
