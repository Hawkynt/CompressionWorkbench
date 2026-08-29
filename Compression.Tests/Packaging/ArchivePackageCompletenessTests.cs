#pragma warning disable CS1591
using System.Xml.Linq;

namespace Compression.Tests.Packaging;

[TestFixture]
public sealed class ArchivePackageCompletenessTests {
  private static readonly string[] ExpectedArchiveProjects = [
    "FileFormat.Acronis",
    "FileFormat.AcronisTibx",
    "FileFormat.Aff4",
    "FileFormat.Afio",
    "FileFormat.Akb",
    "FileFormat.Aomei",
    "FileFormat.AppleSparse",
    "FileFormat.Arsc",
    "FileFormat.Asar",
    "FileFormat.Awb",
    "FileFormat.Bkf",
    "FileFormat.Dar",
    "FileFormat.EaseUs",
    "FileFormat.Ghost",
    "FileFormat.Macrium",
    "FileFormat.Paragon",
    "FileFormat.Partclone",
    "FileFormat.Pfs0",
    "FileFormat.Psf",
    "FileFormat.T64",
    "FileFormat.Veeam",
    "FileFormat.Vib",
  ];

  [Test]
  public void ArchiveMetaPackage_BundlesKnownArchiveDomainProjects() {
    var root = FindRepositoryRoot();
    var packageDir = Path.Combine(root.FullName, "Hawkynt.FileFormats.Archives");
    var projectFile = Path.Combine(packageDir, "Hawkynt.FileFormats.Archives.csproj");
    var localTargets = Path.Combine(packageDir, "Directory.Build.targets");

    Assert.Multiple(() => {
      Assert.That(File.Exists(projectFile), Is.True, $"Missing package project: {projectFile}");
      Assert.That(File.Exists(localTargets), Is.True, $"Missing package-local targets: {localTargets}");
    });

    var includes = LoadProjectReferences(projectFile)
      .Concat(LoadProjectReferences(localTargets))
      .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar)))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    Assert.Multiple(() => {
      foreach (var expected in ExpectedArchiveProjects)
        Assert.That(includes, Does.Contain(expected),
          $"Hawkynt.FileFormats.Archives must bundle {expected}.");
    });
  }

  [Test]
  public void AddedArchivePackageReferences_PointToExistingProjects() {
    var root = FindRepositoryRoot();
    var packageDir = Path.Combine(root.FullName, "Hawkynt.FileFormats.Archives");
    var localTargets = Path.Combine(packageDir, "Directory.Build.targets");

    Assert.Multiple(() => {
      foreach (var include in LoadProjectReferences(localTargets)) {
        var fullPath = Path.GetFullPath(Path.Combine(packageDir, include.Replace('\\', Path.DirectorySeparatorChar)));
        Assert.That(File.Exists(fullPath), Is.True, $"Bundled project reference does not exist: {include}");
      }
    });
  }

  private static IEnumerable<string> LoadProjectReferences(string path) =>
    XDocument.Load(path)
      .Descendants("ProjectReference")
      .Select(element => (string?)element.Attribute("Include"))
      .Where(static include => !string.IsNullOrWhiteSpace(include))
      .Select(static include => include!);

  private static DirectoryInfo FindRepositoryRoot() {
    DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
    while (current is not null) {
      if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))
          && Directory.Exists(Path.Combine(current.FullName, "Hawkynt.FileFormats.Archives")))
        return current;
      current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the CompressionWorkbench repository root from the test directory.");
  }
}
