#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Tds;

/// <summary>
/// Autodesk 3D Studio <c>.3ds</c> — binary chunk-based 3D scene container. Every chunk
/// starts with a 2-byte chunk ID (little-endian) followed by a 4-byte chunk size
/// (little-endian, inclusive of the 6-byte header). Nested chunks share the same
/// header format. Primary chunk at offset 0 has ID <c>0x4D4D</c> ("MM"); common
/// nested chunks include <c>0x3D3D</c> (EDIT3DS), <c>0x4000</c> (OBJ_TRIMESH),
/// <c>0x4100</c> (TRI_MESH), <c>0x4110</c> (TRI_VERTEXPOINT), <c>0x4120</c>
/// (TRI_FACEL), <c>0x4130</c> (TRI_MATERIAL).
/// </summary>
/// <remarks>
/// The project is named <c>FileFormat.Tds</c> because .NET project names cannot start
/// with a digit. References: lib3ds source, various Autodesk 3DS specifications.
/// </remarks>
public sealed class TdsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>Format identifier.</summary>
  public string Id => "Tds";
  /// <summary>Display name.</summary>
  public string DisplayName => "Autodesk 3DS (.3ds)";
  /// <summary>Archive category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Read-only archive capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Default extension.</summary>
  public string DefaultExtension => ".3ds";
  /// <summary>Known extensions.</summary>
  public IReadOnlyList<string> Extensions => [".3ds"];
  /// <summary>No compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Magic: <c>4D 4D</c> at offset 0 — weak (2 bytes), raised by extension match and
  /// the List() implementation's size-consistency check.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4D, 0x4D], Offset: 0, Confidence: 0.55),
  ];
  /// <summary>Stored only.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Not a tar compound format.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Archive family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Short description.</summary>
  public string Description => "Autodesk 3DS chunked 3D scene; surfaces top-level chunks + histogram.";

  /// <summary>Primary chunk ID (M3DMAGIC).</summary>
  public const ushort ChunkMain = 0x4D4D;
  /// <summary>Edit (scene) chunk.</summary>
  public const ushort ChunkEdit = 0x3D3D;
  /// <summary>Named object chunk.</summary>
  public const ushort ChunkObject = 0x4000;
  /// <summary>Triangle mesh (child of OBJECT).</summary>
  public const ushort ChunkTriMesh = 0x4100;
  /// <summary>Vertex list.</summary>
  public const ushort ChunkVertexList = 0x4110;
  /// <summary>Face list.</summary>
  public const ushort ChunkFaceList = 0x4120;

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <inheritdoc />
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.3ds", "Track", blob),
    };

    var chunkHistogram = new Dictionary<ushort, int>();
    var meshCount = 0;
    var vertexCount = 0;
    var faceCount = 0;
    var objectNames = new List<string>();
    var topLevelChunks = new List<(ushort Id, int Start, int Length)>();

    // Validate primary chunk.
    var primarySizeOk = false;
    if (blob.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(blob) == ChunkMain) {
      var primarySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(2));
      if (primarySize == blob.Length) {
        primarySizeOk = true;
        // Walk immediate children of 4D4D (skip the 6-byte header of the main chunk).
        var end = Math.Min(blob.Length, primarySize);
        WalkChildren(blob, 6, end, topLevelChunks, chunkHistogram, ref meshCount, ref vertexCount, ref faceCount, objectNames);
      }
    }

    // Emit top-level chunks.
    foreach (var (id, start, length) in topLevelChunks) {
      if (start + length > blob.Length) continue;
      var chunk = new byte[length];
      Array.Copy(blob, start, chunk, 0, length);
      entries.Add(($"chunk_{id:X4}.bin", "Track", chunk));
    }

    var meta = new StringBuilder();
    meta.AppendLine("; Autodesk 3DS metadata");
    meta.Append("primary_size_matches=").AppendLine(primarySizeOk ? "true" : "false");
    meta.Append("top_level_chunks=").AppendLine(topLevelChunks.Count.ToString(CultureInfo.InvariantCulture));
    meta.Append("mesh_count=").AppendLine(meshCount.ToString(CultureInfo.InvariantCulture));
    meta.Append("vertex_count=").AppendLine(vertexCount.ToString(CultureInfo.InvariantCulture));
    meta.Append("face_count=").AppendLine(faceCount.ToString(CultureInfo.InvariantCulture));
    if (objectNames.Count > 0) meta.Append("object_names=").AppendLine(string.Join(',', objectNames));
    meta.AppendLine();
    meta.AppendLine("; Chunk ID histogram");
    foreach (var kv in chunkHistogram.OrderBy(k => k.Key)) {
      meta.Append("chunk_").Append(kv.Key.ToString("X4", CultureInfo.InvariantCulture)).Append('=').AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
    }
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }

  private static void WalkChildren(byte[] blob, int start, int end,
      List<(ushort Id, int Start, int Length)> topLevel,
      Dictionary<ushort, int> histogram,
      ref int meshCount, ref int vertexCount, ref int faceCount,
      List<string> objectNames) {
    var pos = start;
    while (pos + 6 <= end) {
      var id = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 2));
      if (size < 6 || pos + size > end) break;
      histogram.TryGetValue(id, out var c);
      histogram[id] = c + 1;
      topLevel.Add((id, pos, size));

      // Recurse into interesting containers to gather statistics.
      if (id == ChunkEdit) {
        WalkForStats(blob, pos + 6, pos + size, histogram, ref meshCount, ref vertexCount, ref faceCount, objectNames);
      }
      pos += size;
    }
  }

  private static void WalkForStats(byte[] blob, int start, int end,
      Dictionary<ushort, int> histogram,
      ref int meshCount, ref int vertexCount, ref int faceCount,
      List<string> objectNames) {
    var pos = start;
    while (pos + 6 <= end) {
      var id = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 2));
      if (size < 6 || pos + size > end) break;
      histogram.TryGetValue(id, out var hc);
      histogram[id] = hc + 1;
      var bodyStart = pos + 6;
      var bodyEnd = pos + size;
      if (id == ChunkObject) {
        // Object name is an ASCIIZ string at the start of the body, then sub-chunks.
        var nameEnd = bodyStart;
        while (nameEnd < bodyEnd && blob[nameEnd] != 0) nameEnd++;
        if (nameEnd > bodyStart) objectNames.Add(Encoding.ASCII.GetString(blob, bodyStart, nameEnd - bodyStart));
        var afterName = nameEnd + 1;
        WalkForStats(blob, afterName, bodyEnd, histogram, ref meshCount, ref vertexCount, ref faceCount, objectNames);
      } else if (id == ChunkTriMesh) {
        meshCount++;
        WalkForStats(blob, bodyStart, bodyEnd, histogram, ref meshCount, ref vertexCount, ref faceCount, objectNames);
      } else if (id == ChunkVertexList) {
        if (bodyStart + 2 <= bodyEnd) {
          var n = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(bodyStart));
          vertexCount += n;
        }
      } else if (id == ChunkFaceList) {
        if (bodyStart + 2 <= bodyEnd) {
          var n = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(bodyStart));
          faceCount += n;
        }
      }
      pos += size;
    }
  }
}
