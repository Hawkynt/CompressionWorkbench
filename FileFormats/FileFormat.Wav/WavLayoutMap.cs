#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Wav;

/// <summary>
/// Walks a WAV (RIFF/WAVE) file's chunk structure and emits
/// <see cref="DefragBlockInfo"/> tiles. The RIFF header and fmt chunk are
/// MetadataReserved, the data chunk is Used, and metadata chunks (LIST/INFO,
/// bext, iXML, etc.) are Used with Cold classification.
/// </summary>
public static class WavLayoutMap {

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 12)
      yield break;

    file.Position = 0;
    var header = new byte[12];
    if (file.Read(header, 0, 12) < 12)
      yield break;

    if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F')
      yield break;
    if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E')
      yield break;

    // RIFF header (12 bytes: "RIFF" + size + "WAVE")
    yield return new DefragBlockInfo(0, 12,
      DefragBlockKind.MetadataReserved, "RIFF header", DefragBlockClass.Hot);

    var pos = 12L;
    var chunkHeader = new byte[8];

    while (pos + 8 <= file.Length) {
      file.Position = pos;
      if (file.Read(chunkHeader, 0, 8) < 8)
        break;

      var id = Encoding.ASCII.GetString(chunkHeader, 0, 4);
      var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));

      if (pos + 8 + size > file.Length)
        size = file.Length - pos - 8;

      var (kind, name, cls) = ClassifyChunk(id);
      yield return new DefragBlockInfo(pos, 8 + size, kind, name, cls);

      pos += 8 + size + (size & 1); // word-align
    }
  }

  private static (DefragBlockKind Kind, string Name, DefragBlockClass Class) ClassifyChunk(string id) => id switch {
    "fmt " => (DefragBlockKind.MetadataReserved, "fmt (format)", DefragBlockClass.Hot),
    "data" => (DefragBlockKind.Used, "data (audio)", DefragBlockClass.Normal),
    "fact" => (DefragBlockKind.MetadataReserved, "fact", DefragBlockClass.Normal),
    "LIST" => (DefragBlockKind.Used, "LIST (info)", DefragBlockClass.Cold),
    "bext" => (DefragBlockKind.Used, "bext (broadcast)", DefragBlockClass.Cold),
    "iXML" => (DefragBlockKind.Used, "iXML", DefragBlockClass.Cold),
    "cue " => (DefragBlockKind.MetadataReserved, "cue (markers)", DefragBlockClass.Normal),
    "smpl" => (DefragBlockKind.MetadataReserved, "smpl (sampler)", DefragBlockClass.Normal),
    "JUNK" or "PAD " => (DefragBlockKind.Free, $"{id} (padding)", DefragBlockClass.Normal),
    _ => (DefragBlockKind.Used, id.Trim(), DefragBlockClass.Cold),
  };
}
