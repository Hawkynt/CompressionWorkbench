#:project ../../Hawkynt.FileFormats.FileSystems/Hawkynt.FileFormats.FileSystems.csproj
#:project ../../Compression.Registry/Compression.Registry.csproj
#:property TreatWarningsAsErrors=false
#:property GenerateDocumentationFile=false
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Drives the ReFS reader and offline editor against volumes Windows formatted, for the
// oracle-capture workflow (Windows itself then mounts what this writes and hashes it).
//
//   refs-probe gen <dir>                       deterministic file set to copy onto the volume
//   refs-probe pattern <seed> <length> <out>   the bytes an edit writes, for Windows to write too
//   refs-probe read <image.refs> <out.json>    what our reader lists and extracts (path, size, sha256)
//   refs-probe mutate <image.refs> <op> <out.refs> <result.json>
//        op: rename | rename-long | rename-unicode | replace-same | replace-grow | replace-shrink | replace-grow-small | add | add-dir | remove | remove-small
//            | add-many | add-dir-many | rename-many | replace-many | add-many-split | add-dir1-grow | clone | defrag
//
// Every result is JSON so the PowerShell side can compare it with Get-FileHash.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Compression.Registry;
using FileSystem.Refs;

return args switch {
  ["gen", var dir] => Gen(dir),
  ["pattern", var seed, var length, var output] => WritePattern(int.Parse(seed), int.Parse(length), output),
  ["patterns", var seed, var length, var count, var format] => WritePatterns(int.Parse(seed), int.Parse(length), int.Parse(count), format),
  ["read", var image, var output] => Read(image, output),
  ["mutate", var image, var op, var output, var result] => Mutate(image, op, output, result),
  _ => Usage(),
};

static int Usage() {
  Console.Error.WriteLine("usage: refs-probe gen <dir> | read <image> <out.json> | mutate <image> <op> <out.refs> <result.json>");
  return 2;
}

static byte[] Pattern(int length, int seed) {
  var bytes = new byte[length];
  var x = (uint)(seed * 2654435761u + 12345u);
  for (var i = 0; i < length; i++) {
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    bytes[i] = (byte)x;
  }
  return bytes;
}

static int WritePattern(int seed, int length, string output) {
  File.WriteAllBytes(output, Pattern(length, seed));
  return 0;
}

// count files: file i (named by format) holds Pattern(length + i, seed + i).
static int WritePatterns(int seed, int length, int count, string format) {
  for (var i = 0; i < count; ++i) File.WriteAllBytes(string.Format(format, i), Pattern(length + i, seed + i));
  return 0;
}

static int Gen(string dir) {
  void Put(string path, byte[] data) {
    var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllBytes(full, data);
  }
  Put("small.txt", "Hello ReFS\n"u8.ToArray());
  Put("empty.txt", []);
  Put("dir1/nested/deep.txt", "a file two directories down\n"u8.ToArray());
  Put("data-1m.bin", Pattern(1 << 20, 1));
  Put("rename-me.txt", "rename me\n"u8.ToArray());
  Put("replace-me.bin", Pattern(65536, 3));
  Put("remove-me.bin", Pattern(16384, 4));
  Put("clone-source.bin", Pattern(262144, 5));
  Put("clone-destination.bin", Pattern(262144, 6));
  for (var i = 0; i < 200; i++) Put($"many/f{i:D3}.txt", Encoding.ASCII.GetBytes($"file {i}\n"));
  return 0;
}

static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

static int Read(string image, string output) {
  var descriptor = new RefsFormatDescriptor();
  var tmp = Path.Combine(Path.GetTempPath(), "refs-probe-" + Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(tmp);
  var files = new List<object>();
  string? error = null;
  try {
    using var stream = File.OpenRead(image);
    var entries = descriptor.List(stream, null);
    stream.Position = 0;
    descriptor.Extract(stream, tmp, null, null);
    foreach (var entry in entries.Where(static e => !e.IsDirectory)) {
      var path = Path.Combine(tmp, entry.Name.Replace('/', Path.DirectorySeparatorChar));
      files.Add(new { path = entry.Name, size = entry.OriginalSize, sha256 = File.Exists(path) ? Sha256(File.ReadAllBytes(path)) : null, method = entry.Method });
    }
  } catch (Exception e) {
    error = e.GetType().Name + ": " + e.Message;
  } finally {
    try { Directory.Delete(tmp, true); } catch { /* best effort */ }
  }
  File.WriteAllText(output, JsonSerializer.Serialize(new { error, files }, new JsonSerializerOptions { WriteIndented = true }));
  return 0;
}

static int Mutate(string image, string op, string output, string resultPath) {
  File.Copy(image, output, overwrite: true);
  var descriptor = new RefsFormatDescriptor();
  string outcome;
  object? expectation = null;
  try {
    using var stream = File.Open(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    switch (op) {
      case "rename":
        descriptor.Rename(stream, "rename-me.txt", "renamed.txt");
        expectation = new { renamed = new[] { "rename-me.txt", "renamed.txt" } };
        break;
      case "replace-same": {
        var data = Pattern(65536, 30);
        descriptor.Add(stream, [ArchiveInputInfo.InMemory("replace-me.bin", data)]);
        expectation = new { replaced = "replace-me.bin", size = data.Length, sha256 = Sha256(data) };
        break;
      }
      case "replace-grow": {
        var data = Pattern(300000, 31);
        descriptor.Add(stream, [ArchiveInputInfo.InMemory("replace-me.bin", data)]);
        expectation = new { replaced = "replace-me.bin", size = data.Length, sha256 = Sha256(data) };
        break;
      }
      case "rename-unicode":
        descriptor.Rename(stream, "rename-me.txt", "Gr\u00FC\u00DFe-\u03A9\u03BC\u03AD\u03B3\u03B1-\u65E5\u672C.txt");
        expectation = new { renamed = new[] { "rename-me.txt", "Gr\u00FC\u00DFe-\u03A9\u03BC\u03AD\u03B3\u03B1-\u65E5\u672C.txt" } };
        break;
      case "rename-long":
        descriptor.Rename(stream, "small.txt", "a-much-longer-name-for-the-small-file.txt");
        expectation = new { renamed = new[] { "small.txt", "a-much-longer-name-for-the-small-file.txt" } };
        break;
      case "replace-shrink": {
        var data = Pattern(20000, 32);
        descriptor.Add(stream, [ArchiveInputInfo.InMemory("replace-me.bin", data)]);
        expectation = new { replaced = "replace-me.bin", size = data.Length, sha256 = Sha256(data) };
        break;
      }
      case "replace-grow-small": {
        var data = Pattern(70000, 33);
        descriptor.Add(stream, [ArchiveInputInfo.InMemory("replace-me.bin", data)]);
        expectation = new { replaced = "replace-me.bin", size = data.Length, sha256 = Sha256(data) };
        break;
      }
      case "add": {
        var data = Pattern(30000, 34);
        descriptor.Add(stream, [ArchiveInputInfo.InMemory("added.bin", data)]);
        expectation = new { added = "added.bin", size = data.Length, sha256 = Sha256(data) };
        break;
      }
      case "add-dir":
        descriptor.Add(stream, [new ArchiveInputInfo("new-folder", "new-folder", true)]);
        expectation = new { added = "new-folder/" };
        break;
      case "add-many": {
        var data = Pattern(30000, 34);
        descriptor.Add(stream, [ArchiveInputInfo.InMemory("many/added.bin", data)]);
        expectation = new { added = "many/added.bin", size = data.Length, sha256 = Sha256(data) };
        break;
      }
      case "add-dir-many":
        descriptor.Add(stream, [new ArchiveInputInfo("many/new-folder", "many/new-folder", true)]);
        expectation = new { added = "many/new-folder/" };
        break;
      case "rename-many":
        descriptor.Rename(stream, "many/f010.txt", "many/f010-renamed.txt");
        expectation = new { renamed = new[] { "many/f010.txt", "many/f010-renamed.txt" } };
        break;
      case "add-many-split": {
        var added = new List<object>();
        for (var i = 0; i < 24; ++i) {
          var data = Pattern(5000 + i, 40 + i);
          var name = $"many/f015-{i:D2}.bin";
          descriptor.Add(stream, [ArchiveInputInfo.InMemory(name, data)]);
          added.Add(new { path = name, size = data.Length, sha256 = Sha256(data) });
        }
        expectation = new { addedMany = added };
        break;
      }
      case "add-dir1-grow": {
        var added = new List<object>();
        for (var i = 0; i < 120; ++i) {
          var data = Pattern(3000 + i, 100 + i);
          var name = $"dir1/grow-{i:D3}.bin";
          descriptor.Add(stream, [ArchiveInputInfo.InMemory(name, data)]);
          added.Add(new { path = name, size = data.Length, sha256 = Sha256(data) });
        }
        expectation = new { addedMany = added };
        break;
      }
      case "replace-many": {
        var data = Pattern(70000, 33);
        descriptor.Add(stream, [ArchiveInputInfo.InMemory("many/f020.txt", data)]);
        expectation = new { replaced = "many/f020.txt", size = data.Length, sha256 = Sha256(data) };
        break;
      }
      case "remove-small":
        descriptor.Remove(stream, ["many/f007.txt"]);
        expectation = new { removed = "many/f007.txt" };
        break;
      case "remove":
        descriptor.Remove(stream, ["remove-me.bin"]);
        expectation = new { removed = "remove-me.bin" };
        break;
      case "clone":
        RefsOfflineBlockCloner.CloneWholeFile(stream, "clone-source.bin", "clone-destination.bin");
        expectation = new { cloned = new[] { "clone-source.bin", "clone-destination.bin" } };
        break;
      case "defrag":
        descriptor.Defragment(stream);
        expectation = new { unchanged = true };
        break;
      default:
        throw new ArgumentException("unknown op " + op);
    }
    outcome = "applied";
  } catch (Exception e) {
    outcome = "refused: " + e.GetType().Name + ": " + e.Message;
  }
  var changed = !File.ReadAllBytes(output).AsSpan().SequenceEqual(File.ReadAllBytes(image));
  File.WriteAllText(resultPath, JsonSerializer.Serialize(new { op, outcome, changed, expectation }, new JsonSerializerOptions { WriteIndented = true }));
  return 0;
}
