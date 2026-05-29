#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mp4;

/// <summary>
/// MP4 fast-start optimizer. Moves the <c>moov</c> atom before <c>mdat</c>
/// so browsers and players can begin playback immediately without downloading
/// the entire file. Patches <c>stco</c> and <c>co64</c> chunk-offset tables
/// inside <c>moov</c> to account for the position shift.
/// </summary>
public sealed class Mp4FastStart : IFileInternalChunkMover {

  /// <inheritdoc />
  public void Optimize(Stream file) => Optimize(file, null);

  /// <inheritdoc />
  public void Optimize(Stream file, Compression.Registry.MetadataPlacementProfile? profile) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.CanRead || !file.CanWrite || !file.CanSeek)
      throw new ArgumentException("Stream must be readable, writable, and seekable.", nameof(file));

    file.Position = 0;
    var atoms = WalkTopLevelAtoms(file);

    // Find moov and mdat positions.
    AtomInfo? moov = null, mdat = null;
    foreach (var atom in atoms) {
      if (atom.Type == "moov") moov = atom;
      else if (atom.Type == "mdat") mdat = atom;
    }

    // If either is missing, nothing to do.
    if (moov == null || mdat == null) return;

    // Check profile: if it explicitly says moov → AfterData, don't move it.
    var moovZone = profile?.GetZone("moov");
    if (moovZone == Compression.Registry.PlacementZone.AfterData) return;

    // If moov is already before mdat, it's already fast-start. No-op.
    if (moov.Offset < mdat.Offset) return;

    // Read moov bytes into memory.
    var moovBytes = new byte[moov.Size];
    file.Position = moov.Offset;
    ReadExactly(file, moovBytes, 0, moovBytes.Length);

    // Patch stco/co64 offsets inside moov: add moov.Size to every offset
    // because mdat will be shifted forward by moov.Size bytes.
    var delta = moov.Size;
    PatchChunkOffsets(moovBytes, moov.HeaderSize, moovBytes.Length, delta);

    // Shift mdat (and everything between mdat and moov) forward by moov.Size bytes.
    // We copy from end to start to handle overlap correctly.
    var shiftStart = mdat.Offset;
    var shiftEnd = moov.Offset; // exclusive: bytes [shiftStart, shiftEnd) get shifted
    var shiftLength = shiftEnd - shiftStart;
    ShiftForward(file, shiftStart, shiftLength, delta);

    // Write moov at the old mdat position.
    file.Position = mdat.Offset;
    file.Write(moovBytes, 0, moovBytes.Length);

    file.Flush();
  }

  /// <summary>
  /// Walks top-level atoms in the stream and returns their positions.
  /// </summary>
  public static List<AtomInfo> WalkTopLevelAtoms(Stream file) {
    var result = new List<AtomInfo>();
    var length = file.Length;
    var header = new byte[16];
    var pos = 0L;

    while (pos + 8 <= length) {
      file.Position = pos;
      var read = file.Read(header, 0, Math.Min(16, (int)Math.Min(16, length - pos)));
      if (read < 8) break;

      var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
      var type = Encoding.ASCII.GetString(header, 4, 4);
      var hdr = 8;

      if (size == 1) {
        if (read < 16) break;
        size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        hdr = 16;
      } else if (size == 0) {
        size = length - pos;
      }

      if (size < hdr || pos + size > length) break;

      result.Add(new AtomInfo(type, pos, size, hdr));
      pos += size;
    }

    return result;
  }

  /// <summary>
  /// Recursively patches stco and co64 atoms inside moov. Walks the atom
  /// tree looking for compound containers and leaf offset tables.
  /// </summary>
  public static void PatchChunkOffsets(byte[] data, int start, int end, long delta) {
    var pos = start;
    while (pos + 8 <= end) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      if (size < 8 || pos + size > end) break;
      var type = Encoding.ASCII.GetString(data, pos + 4, 4);

      if (type == "stco") {
        PatchStco(data, pos, size, delta);
      } else if (type == "co64") {
        PatchCo64(data, pos, size, delta);
      } else if (IsCompoundType(type)) {
        // Recurse into compound boxes. Body starts at pos + 8.
        // 'meta' box has a 4-byte version+flags field before children.
        var childStart = pos + 8;
        if (type == "meta" && childStart + 4 <= pos + size)
          childStart += 4;
        PatchChunkOffsets(data, childStart, pos + size, delta);
      }

      pos += size;
    }
  }

  private static void PatchStco(byte[] data, int atomStart, int atomSize, long delta) {
    // stco body: [size:4][type:4][version:1][flags:3][entry_count:4][offsets: N*4]
    var bodyStart = atomStart + 8; // after size+type
    if (bodyStart + 8 > atomStart + atomSize) return;
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 4));
    for (var i = 0; i < count; i++) {
      var offsetPos = bodyStart + 8 + i * 4;
      if (offsetPos + 4 > atomStart + atomSize) break;
      var oldOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offsetPos));
      var newOffset = (uint)(oldOffset + delta);
      BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offsetPos), newOffset);
    }
  }

  private static void PatchCo64(byte[] data, int atomStart, int atomSize, long delta) {
    // co64 body: [size:4][type:4][version:1][flags:3][entry_count:4][offsets: N*8]
    var bodyStart = atomStart + 8;
    if (bodyStart + 8 > atomStart + atomSize) return;
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 4));
    for (var i = 0; i < count; i++) {
      var offsetPos = bodyStart + 8 + i * 8;
      if (offsetPos + 8 > atomStart + atomSize) break;
      var oldOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offsetPos));
      var newOffset = oldOffset + (ulong)delta;
      BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offsetPos), newOffset);
    }
  }

  private static bool IsCompoundType(string type) => type is
    "moov" or "trak" or "mdia" or "minf" or "dinf" or "stbl" or "edts" or
    "udta" or "moof" or "traf" or "mvex" or "meta" or "ipro" or "sinf" or
    "mfra" or "tref";

  /// <summary>
  /// Shifts <paramref name="length"/> bytes starting at <paramref name="srcOffset"/>
  /// forward by <paramref name="delta"/> bytes. Copies from end to start to
  /// handle the overlapping region correctly.
  /// </summary>
  private static void ShiftForward(Stream file, long srcOffset, long length, long delta) {
    const int ChunkSize = 64 * 1024;
    var buffer = new byte[ChunkSize];
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(ChunkSize, remaining);
      var readPos = srcOffset + remaining - chunk;
      var writePos = readPos + delta;
      file.Position = readPos;
      ReadExactly(file, buffer, 0, chunk);
      file.Position = writePos;
      file.Write(buffer, 0, chunk);
      remaining -= chunk;
    }
  }

  private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0) throw new EndOfStreamException("Unexpected end of stream while reading MP4 data.");
      totalRead += read;
    }
  }

  public sealed record AtomInfo(string Type, long Offset, long Size, int HeaderSize);
}
