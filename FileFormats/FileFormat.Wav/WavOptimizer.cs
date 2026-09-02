#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Wav;

/// <summary>
/// WAV optimizer that ensures the data chunk comes immediately after fmt
/// (data-first layout for streaming). Metadata chunks (LIST, bext, iXML, etc.)
/// are moved after the data chunk.
/// </summary>
public sealed class WavOptimizer : IFileInternalChunkMover {

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

    if (data.Length < 44) return;
    if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return;
    if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return;

    // Parse all chunks
    var chunks = new List<(string Id, int Start, int TotalSize)>();
    var pos = 12;
    while (pos + 8 <= data.Length) {
      var id = Encoding.ASCII.GetString(data, pos, 4);
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
      if (pos + 8 + size > data.Length) size = data.Length - pos - 8;
      var totalSize = 8 + size + (size & 1);
      if (pos + totalSize > data.Length) totalSize = data.Length - pos;
      chunks.Add((id, pos, totalSize));
      pos += totalSize;
    }

    // Optimal order: fmt first, then data, then everything else
    var ordered = new List<(string Id, int Start, int TotalSize)>();
    var fmtChunk = chunks.FirstOrDefault(c => c.Id == "fmt ");
    var dataChunk = chunks.FirstOrDefault(c => c.Id == "data");
    var factChunk = chunks.FirstOrDefault(c => c.Id == "fact");

    if (fmtChunk.Id == null || dataChunk.Id == null) return;

    // Check if already optimal (fmt -> [fact] -> data -> rest)
    var fmtIdx = chunks.FindIndex(c => c.Id == "fmt ");
    var dataIdx = chunks.FindIndex(c => c.Id == "data");

    // If fmt is first and data is right after (or after fact), already optimal
    if (fmtIdx == 0) {
      var expectedDataIdx = factChunk.Id != null && chunks.FindIndex(c => c.Id == "fact") == 1 ? 2 : 1;
      if (dataIdx == expectedDataIdx) return;
    }

    // Partition metadata chunks based on profile.
    var metaBefore = new List<(string Id, int Start, int TotalSize)>();
    var metaAfter = new List<(string Id, int Start, int TotalSize)>();
    foreach (var c in chunks) {
      if (c.Id is "fmt " or "data" or "fact") continue;
      var zone = profile?.GetZone(c.Id);
      if (zone == Compression.Registry.PlacementZone.BeforeData)
        metaBefore.Add(c);
      else
        metaAfter.Add(c); // default: after data (data-first layout)
    }

    ordered.Add(fmtChunk);
    if (factChunk.Id != null) ordered.Add(factChunk);
    ordered.AddRange(metaBefore);
    ordered.Add(dataChunk);
    ordered.AddRange(metaAfter);

    // Build new file
    var result = new MemoryStream();
    result.Write(data, 0, 12); // RIFF header
    foreach (var (_, start, totalSize) in ordered)
      result.Write(data, start, totalSize);

    // Update RIFF size
    var resultData = result.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(resultData.AsSpan(4), (uint)(resultData.Length - 8));

    file.Position = 0;
    file.SetLength(resultData.Length);
    file.Write(resultData);
    file.Flush();
  }
}
