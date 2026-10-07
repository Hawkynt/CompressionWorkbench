#pragma warning disable CS1591
using System.Reflection;
using Compression.Registry;

namespace Compression.Tests.Fla;

/// <summary>
/// The FLA files and the XFL project saved by Flash Professional CS6 that the reader and writer are
/// judged against, plus what xfl2svg made of each (see <c>ReferenceVectors/README.md</c>). They are
/// embedded so a runner whose content files failed to deploy cannot pass by finding nothing.
/// </summary>
internal static class FlaReferenceVectors {

  /// <summary>The compressed FLA files saved by Flash Professional CS6.</summary>
  public static readonly string[] AdobeFlaFiles = ["run_as2.fla", "slash_syntax.fla", "namespaces.fla"];

  private const string Prefix = "FlaVectors.";

  private static string[] ResourceNames
    => Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(static n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();

  private static byte[] ReadResource(string resourceName) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException($"Reference vector '{resourceName}' is not embedded.");
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  // MSBuild puts the host's separator into %(RecursiveDir).
  private static string Normalize(string resourceName) => resourceName[Prefix.Length..].Replace('\\', '/');

  public static byte[] Bytes(string name)
    => ReadResource(ResourceNames.Single(n => Normalize(n) == name));

  /// <summary>xfl2svg's captured report for a vector, one line per fact.</summary>
  public static string[] Report(string name)
    => System.Text.Encoding.UTF8.GetString(Bytes(name + ".xfl2svg.txt")).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

  /// <summary>Every file of an uncompressed XFL project folder, keyed by its '/'-separated path inside it.</summary>
  public static SortedDictionary<string, byte[]> Project(string folder) {
    var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var resource in ResourceNames) {
      var name = Normalize(resource);
      if (name.StartsWith(folder + "/", StringComparison.Ordinal))
        files[name[(folder.Length + 1)..]] = ReadResource(resource);
    }
    return files;
  }

  /// <summary>Writes a project to disk the way a user's XFL folder looks.</summary>
  public static void WriteProject(IReadOnlyDictionary<string, byte[]> files, string root) {
    foreach (var (name, data) in files) {
      var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
  }

  /// <summary>The inputs the shell hands to Create for a folder: every sub-folder, then every file.</summary>
  public static ArchiveInputInfo[] InputsFromFolder(string root) {
    static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    return [
      .. Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(d => new ArchiveInputInfo(d, Relative(root, d), IsDirectory: true)),
      .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(f => ArchiveInputInfo.FromFile(new FileInfo(f), Relative(root, f))),
    ];
  }

  public static string Sha256(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
}
