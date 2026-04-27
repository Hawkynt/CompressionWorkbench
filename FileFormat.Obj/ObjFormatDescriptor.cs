#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Obj;

/// <summary>
/// Wavefront OBJ (<c>.obj</c>) — text 3D format where <c>o &lt;name&gt;</c> starts a new
/// object and <c>g &lt;name&gt;</c> starts a new group. Archive view: <c>FULL.obj</c>
/// plus one sub-OBJ per object (or group when no <c>o</c> lines exist).
/// <para>
/// Each sub-OBJ retains any pre-object preamble (vertex/uv/normal tables + mtllib
/// references) so the emitted slices are geometrically valid on their own. This
/// mirrors how slicing a multi-frame GIF rebuilds a standalone single-frame GIF.
/// </para>
/// </summary>
public sealed class ObjFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Obj";
  public string DisplayName => "Wavefront OBJ (3D model)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".obj";
  public IReadOnlyList<string> Extensions => [".obj"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // OBJ is plain text without a magic byte sequence; extension-based only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Wavefront OBJ; per-object / per-group slices + metadata.";

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
      ("FULL.obj", "Track", blob),
    };

    var text = Encoding.UTF8.GetString(blob);
    var lines = text.Replace("\r\n", "\n").Split('\n');

    // Collect the pre-object preamble (anything before the first "o " or "g " line).
    var preamble = new StringBuilder();
    var objects = new List<(string Name, List<string> Lines)>();
    List<string>? current = null;
    string currentName = "";

    foreach (var line in lines) {
      var trimmed = line.TrimStart();
      if (trimmed.StartsWith("o ", StringComparison.Ordinal) || trimmed.StartsWith("g ", StringComparison.Ordinal)) {
        if (current != null) objects.Add((currentName, current));
        currentName = trimmed[2..].Trim();
        current = new List<string> { line };
      } else if (current != null) {
        current.Add(line);
      } else {
        preamble.AppendLine(line);
      }
    }
    if (current != null) objects.Add((currentName, current));

    if (objects.Count > 1 || (objects.Count == 1 && !string.IsNullOrWhiteSpace(objects[0].Name))) {
      for (var i = 0; i < objects.Count; ++i) {
        var (name, objLines) = objects[i];
        var content = preamble.ToString() + string.Join('\n', objLines) + "\n";
        var safeName = string.IsNullOrWhiteSpace(name)
          ? $"object_{i:D3}"
          : Sanitize(name);
        entries.Add(($"{safeName}.obj", "Track", Encoding.UTF8.GetBytes(content)));
      }
    }

    var ini = new StringBuilder();
    ini.AppendLine("; Wavefront OBJ summary");
    ini.Append("objects=").AppendLine(objects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    ini.Append("lines=").AppendLine(lines.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    var vertexCount = lines.Count(l => l.TrimStart().StartsWith("v ", StringComparison.Ordinal));
    var faceCount = lines.Count(l => l.TrimStart().StartsWith("f ", StringComparison.Ordinal));
    ini.Append("vertices=").AppendLine(vertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    ini.Append("faces=").AppendLine(faceCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    var mtllib = lines.FirstOrDefault(l => l.TrimStart().StartsWith("mtllib ", StringComparison.Ordinal))?.TrimStart()[7..].Trim();
    if (!string.IsNullOrWhiteSpace(mtllib)) ini.Append("material_library=").AppendLine(mtllib);
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    return entries;
  }

  private static string Sanitize(string s) {
    var sb = new StringBuilder(Math.Min(s.Length, 40));
    foreach (var c in s) {
      if (sb.Length >= 40) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().Trim('_') : "object";
  }
}
