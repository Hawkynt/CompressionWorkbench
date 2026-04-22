#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Compression.Registry;

namespace FileFormat.Glb;

/// <summary>
/// Binary glTF (<c>.glb</c>) container: 12-byte header + JSON chunk + optional
/// BIN chunk. Surfaces the JSON as <c>scene.gltf</c> (so any glTF viewer can
/// open it), the binary buffer as <c>binary.bin</c>, and — best-effort — any
/// embedded images referenced by the JSON's <c>images</c> section as
/// <c>images/&lt;name&gt;.&lt;ext&gt;</c>.
/// </summary>
public sealed class GlbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Glb";
  public string DisplayName => "GLB (binary glTF)";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".glb";
  public IReadOnlyList<string> Extensions => [".glb", ".vrm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("glTF"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Binary glTF 3D scene; JSON + binary buffer + embedded images.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.glb", "Track", blob),
    };
    if (blob.Length < 12) return entries;
    if (blob[0] != 'g' || blob[1] != 'l' || blob[2] != 'T' || blob[3] != 'F') return entries;

    // GLB header: magic (4) + version (4 LE) + total length (4 LE).
    var totalLen = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
    var end = Math.Min((int)totalLen, blob.Length);

    byte[]? json = null;
    byte[]? bin = null;
    var pos = 12;
    while (pos + 8 <= end) {
      var chunkLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos));
      var type = Encoding.ASCII.GetString(blob.AsSpan(pos + 4, 4));
      var body = blob.AsSpan(pos + 8, Math.Min(chunkLen, end - pos - 8)).ToArray();
      // JSON padding byte is 0x20; BIN padding is 0x00 — trim trailing padding for the surfaced bytes.
      if (type == "JSON") json = TrimPadding(body, 0x20);
      else if (type[0] == 'B' && type[1] == 'I' && type[2] == 'N') bin = TrimPadding(body, 0x00);
      pos += 8 + chunkLen;
    }

    if (json != null) entries.Add(("scene.gltf", "Track", json));
    if (bin != null) entries.Add(("binary.bin", "Track", bin));

    // Best-effort: parse JSON, emit bufferViews-backed images as extractable files.
    if (json != null && bin != null) {
      try {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("images", out var images) &&
            doc.RootElement.TryGetProperty("bufferViews", out var bvs)) {
          var bvList = bvs.EnumerateArray().ToArray();
          var imgIdx = 0;
          foreach (var img in images.EnumerateArray()) {
            if (!img.TryGetProperty("bufferView", out var bvRef)) { ++imgIdx; continue; }
            var bvi = bvRef.GetInt32();
            if (bvi < 0 || bvi >= bvList.Length) { ++imgIdx; continue; }
            var bv = bvList[bvi];
            var bvOff = bv.TryGetProperty("byteOffset", out var oEl) ? oEl.GetInt32() : 0;
            var bvLen = bv.GetProperty("byteLength").GetInt32();
            if (bvOff + bvLen > bin.Length) { ++imgIdx; continue; }
            var mime = img.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
            var ext = mime switch {
              "image/png" => ".png",
              "image/jpeg" => ".jpg",
              "image/webp" => ".webp",
              _ => ".bin",
            };
            var name = img.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s
              ? Sanitize(s) : $"image_{imgIdx:D3}";
            entries.Add(($"images/{name}{ext}", "Track", bin.AsSpan(bvOff, bvLen).ToArray()));
            ++imgIdx;
          }
        }
      } catch { /* best effort — malformed JSON means we just skip the image walk */ }
    }

    return entries;
  }

  private static byte[] TrimPadding(byte[] body, byte pad) {
    var end = body.Length;
    while (end > 0 && body[end - 1] == pad) --end;
    return body.AsSpan(0, end).ToArray();
  }

  private static string Sanitize(string s) {
    var sb = new StringBuilder(Math.Min(s.Length, 40));
    foreach (var c in s) {
      if (sb.Length >= 40) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().Trim('_') : "image";
  }
}
