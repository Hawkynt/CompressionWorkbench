#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FileFormat.Asar;

/// <summary>
/// Builder for Electron <c>.asar</c> archives. Files are concatenated back to
/// back (no per-file padding, matching the reference <c>asar</c> tool) and their
/// header <c>offset</c> is the running byte position relative to the end of the
/// header. The JSON header is wrapped in the two-pickle prelude and padded to a
/// 4-byte boundary.
/// </summary>
public sealed class AsarWriter {

  private readonly List<(string Path, byte[] Data, bool Executable)> _files = [];
  private readonly List<string> _dirs = [];

  /// <summary>Queues a file at the given archive-relative path (forward slashes).</summary>
  public void AddFile(string path, byte[] data, bool executable = false) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((Normalize(path), data, executable));
  }

  /// <summary>Records an (optionally empty) directory so it survives a round-trip.</summary>
  public void AddDirectory(string path) {
    ArgumentNullException.ThrowIfNull(path);
    this._dirs.Add(Normalize(path));
  }

  /// <summary>Serialises the archive to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var root = new JsonObject { ["files"] = new JsonObject() };

    foreach (var dir in this._dirs)
      EnsureDirectory(root, dir);

    long running = 0;
    foreach (var (path, data, executable) in this._files) {
      var parts = path.Split('/');
      var container = DescendToParent(root, parts);
      var fileNode = new JsonObject {
        ["size"] = data.Length,
        ["offset"] = running.ToString(CultureInfo.InvariantCulture),
      };
      if (executable) fileNode["executable"] = true;
      container[parts[^1]] = fileNode;
      running += data.Length;
    }

    var jsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
    var strLen = jsonBytes.Length;
    var aligned = (strLen + 3) & ~3;
    var headerPayloadSize = 4 + aligned;       // header pickle payload
    var headerBufLen = 4 + headerPayloadSize;  // = 8 + aligned

    Span<byte> prelude = stackalloc byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(prelude[0..], 4);
    BinaryPrimitives.WriteUInt32LittleEndian(prelude[4..], (uint)headerBufLen);
    BinaryPrimitives.WriteUInt32LittleEndian(prelude[8..], (uint)headerPayloadSize);
    BinaryPrimitives.WriteUInt32LittleEndian(prelude[12..], (uint)strLen);
    output.Write(prelude);
    output.Write(jsonBytes);
    if (aligned > strLen) output.Write(new byte[aligned - strLen]);

    foreach (var (_, data, _) in this._files)
      output.Write(data);
  }

  private static JsonObject DescendToParent(JsonObject root, string[] parts) {
    var container = (JsonObject)root["files"]!;
    for (var i = 0; i < parts.Length - 1; i++)
      container = ChildFiles(container, parts[i]);
    return container;
  }

  private static void EnsureDirectory(JsonObject root, string dir) {
    var container = (JsonObject)root["files"]!;
    foreach (var seg in dir.Split('/'))
      container = ChildFiles(container, seg);
  }

  private static JsonObject ChildFiles(JsonObject container, string name) {
    if (container[name] is not JsonObject node) {
      node = new JsonObject { ["files"] = new JsonObject() };
      container[name] = node;
    }
    return (JsonObject)node["files"]!;
  }

  private static string Normalize(string path)
    => path.Replace('\\', '/').Trim('/');
}
