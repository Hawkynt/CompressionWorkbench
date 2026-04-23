#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Stl;

/// <summary>
/// STL (stereolithography) 3D model. Two variants:
/// <list type="bullet">
/// <item>ASCII — starts with <c>solid &lt;name&gt;\n</c> then <c>facet normal</c>…/<c>endfacet</c> blocks.</item>
/// <item>Binary — 80-byte header + <c>uint32 LE</c> triangle count + 50 bytes per triangle
/// (12-byte normal + 3×12-byte vertices + 2-byte attribute).</item>
/// </list>
/// Binary detection: <c>filesize == 84 + 50 * triCount</c>. ASCII detection: leading
/// <c>solid </c> plus <c>facet normal</c> in the first ~1 KB (needed because some malformed
/// binary files begin with the text "solid"). Surfaces <c>metadata.ini</c> (variant,
/// triangle count, object name, bounding box) and <c>triangles.bin</c> (raw binary
/// facet block — for ASCII this is reconstructed from parsed vertices).
/// </summary>
public sealed class StlFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>Format identifier.</summary>
  public string Id => "Stl";
  /// <summary>Display name.</summary>
  public string DisplayName => "STL (stereolithography)";
  /// <summary>Archive category — surfaces synthesised body entries.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Read-only archive capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Default extension.</summary>
  public string DefaultExtension => ".stl";
  /// <summary>Known extensions.</summary>
  public IReadOnlyList<string> Extensions => [".stl"];
  /// <summary>No compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Binary STL's 80-byte header is user-provided and has no magic; extension-primary.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>Stored only.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Not a tar compound format.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Archive family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Short description.</summary>
  public string Description => "STL 3D model (ASCII or binary); surfaces triangle block + metadata.";

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
      ("FULL.stl", "Track", blob),
    };

    var (variant, triCount, name, triangles) = ParseStl(blob);

    // Bounding box.
    var hasAny = triangles.Count > 0;
    float minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
    if (hasAny) {
      minX = minY = minZ = float.PositiveInfinity;
      maxX = maxY = maxZ = float.NegativeInfinity;
      foreach (var t in triangles) {
        for (var v = 0; v < 3; v++) {
          var x = t.Vertices[v * 3];
          var y = t.Vertices[v * 3 + 1];
          var z = t.Vertices[v * 3 + 2];
          if (x < minX) minX = x; if (x > maxX) maxX = x;
          if (y < minY) minY = y; if (y > maxY) maxY = y;
          if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
      }
    }

    // Reconstruct binary triangle block (consistent output for both variants).
    var trianglesBin = BuildBinaryTriangleBlock(triangles);
    entries.Add(("triangles.bin", "Track", trianglesBin));

    var meta = new StringBuilder();
    meta.AppendLine("; STL metadata");
    meta.Append("variant=").AppendLine(variant);
    meta.Append("triangle_count=").AppendLine(triCount.ToString(CultureInfo.InvariantCulture));
    meta.Append("name=").AppendLine(name ?? "");
    if (hasAny) {
      meta.Append("bbox_min=").AppendLine($"{minX.ToString(CultureInfo.InvariantCulture)},{minY.ToString(CultureInfo.InvariantCulture)},{minZ.ToString(CultureInfo.InvariantCulture)}");
      meta.Append("bbox_max=").AppendLine($"{maxX.ToString(CultureInfo.InvariantCulture)},{maxY.ToString(CultureInfo.InvariantCulture)},{maxZ.ToString(CultureInfo.InvariantCulture)}");
    }
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }

  internal sealed record Triangle(float[] Normal, float[] Vertices);

  private static (string Variant, int TriCount, string? Name, List<Triangle> Triangles) ParseStl(byte[] blob) {
    // Binary detection: file size check.
    if (blob.Length >= 84) {
      var triCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(80, 4));
      if (triCount > 0 && triCount < 100_000_000 && blob.Length == 84L + 50L * triCount) {
        return ("binary", (int)triCount, null, ParseBinary(blob, (int)triCount));
      }
    }
    // ASCII check.
    if (IsAsciiStl(blob)) {
      return ParseAscii(blob);
    }
    // Unknown — empty result, but still report variant=unknown for callers.
    return ("unknown", 0, null, []);
  }

  private static bool IsAsciiStl(byte[] blob) {
    if (blob.Length < 7) return false;
    // Leading "solid "
    if (blob[0] != 's' || blob[1] != 'o' || blob[2] != 'l' || blob[3] != 'i' || blob[4] != 'd') return false;
    // Look for "facet normal" in first 1 KB.
    var scanEnd = Math.Min(blob.Length, 1024);
    var haystack = Encoding.ASCII.GetString(blob, 0, scanEnd);
    return haystack.Contains("facet normal", StringComparison.OrdinalIgnoreCase);
  }

  private static List<Triangle> ParseBinary(byte[] blob, int triCount) {
    var result = new List<Triangle>(triCount);
    var pos = 84;
    for (var i = 0; i < triCount && pos + 50 <= blob.Length; i++) {
      var normal = new float[3];
      var verts = new float[9];
      for (var j = 0; j < 3; j++) normal[j] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(pos + j * 4, 4));
      for (var j = 0; j < 9; j++) verts[j] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(pos + 12 + j * 4, 4));
      result.Add(new Triangle(normal, verts));
      pos += 50;
    }
    return result;
  }

  private static (string Variant, int TriCount, string? Name, List<Triangle> Triangles) ParseAscii(byte[] blob) {
    var text = Encoding.ASCII.GetString(blob);
    var lines = text.Replace("\r\n", "\n").Split('\n');
    string? name = null;
    var triangles = new List<Triangle>();
    var i = 0;

    if (i < lines.Length) {
      var first = lines[i].TrimStart();
      if (first.StartsWith("solid", StringComparison.OrdinalIgnoreCase)) {
        name = first.Length > 5 ? first[5..].Trim() : null;
        i++;
      }
    }

    while (i < lines.Length) {
      var line = lines[i].Trim();
      if (line.StartsWith("endsolid", StringComparison.OrdinalIgnoreCase)) break;
      if (line.StartsWith("facet", StringComparison.OrdinalIgnoreCase)) {
        var normal = new float[3];
        var verts = new float[9];
        var vi = 0;
        // Parse "facet normal nx ny nz"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 5) {
          _ = float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out normal[0]);
          _ = float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out normal[1]);
          _ = float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out normal[2]);
        }
        i++;
        while (i < lines.Length && !lines[i].Trim().StartsWith("endfacet", StringComparison.OrdinalIgnoreCase)) {
          var vline = lines[i].Trim();
          if (vline.StartsWith("vertex", StringComparison.OrdinalIgnoreCase) && vi < 3) {
            var vp = vline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (vp.Length >= 4) {
              _ = float.TryParse(vp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out verts[vi * 3]);
              _ = float.TryParse(vp[2], NumberStyles.Float, CultureInfo.InvariantCulture, out verts[vi * 3 + 1]);
              _ = float.TryParse(vp[3], NumberStyles.Float, CultureInfo.InvariantCulture, out verts[vi * 3 + 2]);
              vi++;
            }
          }
          i++;
        }
        triangles.Add(new Triangle(normal, verts));
      }
      i++;
    }

    return ("ascii", triangles.Count, name, triangles);
  }

  private static byte[] BuildBinaryTriangleBlock(List<Triangle> triangles) {
    var buf = new byte[triangles.Count * 50];
    var pos = 0;
    foreach (var t in triangles) {
      for (var j = 0; j < 3; j++) BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + j * 4), t.Normal[j]);
      for (var j = 0; j < 9; j++) BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 12 + j * 4), t.Vertices[j]);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos + 48), 0);
      pos += 50;
    }
    return buf;
  }
}
