using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Compression.Tests.Documentation;

namespace Compression.Tests.Architecture;

/// <summary>
/// Product code carries its own codecs: nothing outside the tests may lean on the platform's
/// <c>System.IO.Compression</c> (Deflate/GZip/ZLib/Brotli streams, ZipArchive, ZipFile), whose
/// output differs between operating systems and runtime versions.
/// </summary>
/// <remarks>
/// <para>Two checks, because neither covers everything alone. The compiled product assemblies
/// next to this test are inspected for type and assembly references into
/// <c>System.IO.Compression*</c> — that catches a <c>global using</c>, a fully qualified name or
/// a reference pulled in through a shared props file, none of which a text search sees reliably.
/// The sources of every product project in the solution (and every project they reference) are
/// searched as well, which reaches the apps, SFX stubs and generators this test project does
/// not reference.</para>
/// <para>Tests are exempt: there the platform codecs are an independent oracle. So is
/// <c>Vendored/**</c>, third-party code kept byte-identical to its upstream; none of it uses
/// the platform codecs today. An assembly that ever compiles such vendored code would show up
/// in the assembly check and has to be listed in <see cref="AllowedAssemblies"/> with the
/// reason. Third-party NuGet packages are not ours to change and are not inspected; neither
/// is the CLI's Costura-injected loader, which inflates the assemblies Costura itself embedded
/// at build time and never touches user data.</para>
/// </remarks>
[TestFixture]
public class NoPlatformCompressionTests {

  /// <summary>Product assemblies allowed to reference the platform codecs, with the reason. Empty on purpose.</summary>
  private static readonly Dictionary<string, string> AllowedAssemblies = new(StringComparer.OrdinalIgnoreCase);

  private static readonly string[] NonProductProjects = ["Compression.Tests", "Compression.Benchmarks"];

  private static readonly string[] RequiredAssemblies = [
    "Compression.Registry", "Hawkynt.Compression.Core", "Compression.Lib",
    "Hawkynt.FileFormats.Archives", "Hawkynt.FileFormats.Audio", "Hawkynt.FileFormats.FileSystems",
  ];

  private static readonly Regex PlatformCompression = new(@"\bSystem\.IO\.Compression\b", RegexOptions.Compiled);

  [Test, Category("Spec")]
  public void ProductAssemblies_ReferenceNoPlatformCompressionType() {
    var directory = TestContext.CurrentContext.TestDirectory;
    var inspected = new List<string>();
    var offenders = new List<string>();

    foreach (var assemblyName in ProductProjects().Select(p => p.AssemblyName).Distinct(StringComparer.OrdinalIgnoreCase)) {
      var path = Path.Combine(directory, assemblyName + ".dll");
      if (!File.Exists(path))
        continue;

      inspected.Add(assemblyName);
      if (AllowedAssemblies.ContainsKey(assemblyName))
        continue;

      offenders.AddRange(PlatformCompressionReferences(path).Select(reference => $"{assemblyName}: {reference}"));
    }

    Assert.That(inspected, Is.SupersetOf(RequiredAssemblies), "the product assemblies were not found next to the tests");
    Assert.That(offenders, Is.Empty, "product code must use its own codecs, not System.IO.Compression");
  }

  [Test, Category("Spec")]
  public void ProductSources_DoNotNamePlatformCompression() {
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var files = ProductProjects()
      .Select(p => p.Directory)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .SelectMany(SourceFiles)
      .Concat(Directory.EnumerateFiles(root, "Directory.Build.*"));

    var offenders = new List<string>();
    foreach (var file in files) {
      var lines = File.ReadAllLines(file);
      for (var i = 0; i < lines.Length; ++i)
        if (PlatformCompression.IsMatch(lines[i]))
          offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
    }

    Assert.That(offenders, Is.Empty, "product code must use its own codecs, not System.IO.Compression");
  }

  [Test, Category("Spec")]
  public void TheAssemblyCheck_FindsAReferenceWhereThereIsOne() {
    // The tests themselves use the platform codecs as an oracle, so the check must see them.
    var self = typeof(NoPlatformCompressionTests).Assembly.Location;
    Assert.That(PlatformCompressionReferences(self), Is.Not.Empty);
  }

  private static IEnumerable<string> PlatformCompressionReferences(string path) {
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var found = new SortedSet<string>(StringComparer.Ordinal);

    foreach (var handle in metadata.AssemblyReferences) {
      var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
      if (name.StartsWith("System.IO.Compression", StringComparison.Ordinal))
        found.Add("assembly reference " + name);
    }

    foreach (var handle in metadata.TypeReferences) {
      var type = metadata.GetTypeReference(handle);
      var ns = metadata.GetString(type.Namespace);
      if (ns.StartsWith("System.IO.Compression", StringComparison.Ordinal))
        found.Add($"type {ns}.{metadata.GetString(type.Name)}");
    }

    return found;
  }

  private static IEnumerable<string> SourceFiles(string projectDirectory) {
    var pending = new Stack<string>([projectDirectory]);
    while (pending.Count > 0) {
      var directory = pending.Pop();
      foreach (var sub in Directory.EnumerateDirectories(directory)) {
        var name = Path.GetFileName(sub);
        if (name is "bin" or "obj" or "Vendored" or ".claude" or ".git")
          continue;
        pending.Push(sub);
      }

      foreach (var file in Directory.EnumerateFiles(directory))
        if (Path.GetExtension(file) is ".cs" or ".csproj" or ".props" or ".targets")
          yield return file;
    }
  }

  /// <summary>Every project in the solution except the tests and benchmarks, plus every project those reference.</summary>
  private static List<(string Directory, string AssemblyName)> ProductProjects() {
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var solution = XDocument.Load(Directory.EnumerateFiles(root, "*.slnx").Single());
    var pending = new Queue<string>(solution.Descendants("Project")
      .Select(p => Path.GetFullPath(Path.Combine(root, (string)p.Attribute("Path")!)))
      .Where(p => !NonProductProjects.Contains(Path.GetFileNameWithoutExtension(p))));

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var result = new List<(string, string)>();
    while (pending.Count > 0) {
      var project = pending.Dequeue();
      if (!seen.Add(project) || !File.Exists(project))
        continue;

      var document = XDocument.Load(project);
      var assemblyName = document.Descendants("AssemblyName").Select(e => e.Value.Trim()).LastOrDefault(v => v.Length > 0 && !v.Contains('$'))
                         ?? Path.GetFileNameWithoutExtension(project);
      result.Add((Path.GetDirectoryName(project)!, assemblyName));

      foreach (var reference in document.Descendants("ProjectReference")) {
        var include = (string?)reference.Attribute("Include");
        if (include is null || include.Contains('$'))
          continue;
        var referenced = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)));
        if (!NonProductProjects.Contains(Path.GetFileNameWithoutExtension(referenced)))
          pending.Enqueue(referenced);
      }
    }

    return result;
  }
}
