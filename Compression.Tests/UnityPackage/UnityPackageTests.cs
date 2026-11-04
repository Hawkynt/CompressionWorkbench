using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.UnityPackage;

namespace Compression.Tests.UnityPackage;

/// <summary>
/// Unity packages. The fixture is Morpeh's <c>ScriptTemplates.unitypackage</c>, exported by the Unity
/// editor; <c>morpeh-listing.txt</c> is Python's <c>tarfile</c> reading of it (path, GUID, asset size and
/// SHA-256, .meta SHA-256). Packages we write are read back by the same Python script, and by
/// <c>unitypackage_extractor</c> when it is installed. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class UnityPackageTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"UnityPackageVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded Unity package vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>Python's tarfile view of a package, one line per asset: path guid assetSize|-1 assetSha|- metaSha|-.</summary>
  private const string ListingScript = """
    import tarfile, hashlib, sys
    t = tarfile.open(sys.argv[1])
    d = {}
    for m in t.getmembers():
        if m.isfile():
            g, f = m.name.split('/', 1)
            d.setdefault(g, {})[f] = t.extractfile(m).read()
    rows = []
    for g, f in d.items():
        p = f['pathname'].decode('utf-8').split('\n')[0]
        a = f.get('asset'); me = f.get('asset.meta')
        rows.append((p, g, len(a) if a is not None else -1, hashlib.sha256(a).hexdigest() if a is not None else '-',
                     hashlib.sha256(me).hexdigest() if me else '-'))
    for r in sorted(rows): print(*r)
    """;

  private static string Sha(byte[]? data) => data == null ? "-" : Convert.ToHexStringLower(SHA256.HashData(data));

  private static void AssertMatchesListing(IReadOnlyList<UnityPackageAsset> assets, string listing) {
    var rows = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(' ')).ToList();
    Assert.That(assets.Select(a => a.Path), Is.EqualTo(rows.Select(r => r[0])));
    for (var i = 0; i < rows.Count; ++i) {
      var a = assets[i];
      Assert.That(a.Guid, Is.EqualTo(rows[i][1]), a.Path);
      Assert.That((a.Asset?.Length ?? -1).ToString(), Is.EqualTo(rows[i][2]), a.Path);
      Assert.That(Sha(a.Asset), Is.EqualTo(rows[i][3]), a.Path);
      Assert.That(Sha(a.Meta), Is.EqualTo(rows[i][4]), a.Path);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_UnityExportedPackage_When_Read_Then_MatchesTarfileListing()
    => AssertMatchesListing(FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(Vector("morpeh-ScriptTemplates.unitypackage"))),
      Encoding.UTF8.GetString(Vector("morpeh-listing.txt")));

  [Test, Category("ThirdPartyVector")]
  public void Given_UnityExportedPackage_When_Listed_Then_AssetsMetasAndFolder() {
    var list = new UnityPackageFormatDescriptor().List(new MemoryStream(Vector("morpeh-ScriptTemplates.unitypackage")), null);
    Assert.That(list, Has.Count.EqualTo(18));
    Assert.That(list.Single(e => e.Name == "Assets/ScriptTemplates").IsDirectory, Is.True);
    Assert.That(list.Select(e => e.Name), Does.Contain("Assets/ScriptTemplates.meta"));
    Assert.That(list.Single(e => e.Name == "Assets/ScriptTemplates/ECS__System.cs.txt").OriginalSize, Is.EqualTo(457));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_UnityExportedPackage_When_Extracted_Then_LaidOutAsUnityImportsIt() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_upkg_{Guid.NewGuid():N}");
    try {
      new UnityPackageFormatDescriptor().Extract(new MemoryStream(Vector("morpeh-ScriptTemplates.unitypackage")), dir, null, null);
      Assert.That(Directory.Exists(Path.Combine(dir, "Assets", "ScriptTemplates")), Is.True);
      Assert.That(Sha(File.ReadAllBytes(Path.Combine(dir, "Assets", "ScriptTemplates", "ECS__System.cs.txt"))),
        Is.EqualTo("34eb27a41fa8c7fedc277bc808e3ef788fb4dadfd194174e65b20387fefe55b3"));
      Assert.That(File.ReadAllText(Path.Combine(dir, "Assets", "ScriptTemplates.meta")), Does.Contain("guid: 6720cd542448c5a4a9f11ad2f76c6141"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Given_UnityPackage_When_RewrittenUnchanged_Then_EveryAssetKeepsGuidContentAndMeta() {
    var original = FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(Vector("morpeh-ScriptTemplates.unitypackage")));
    using var ms = new MemoryStream();
    FileFormat.UnityPackage.UnityPackage.Write(ms, original);
    var again = FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(ms.ToArray()));
    AssertMatchesListing(again, Encoding.UTF8.GetString(Vector("morpeh-listing.txt")));
  }

  [Test, Category("RoundTrip")]
  public void Given_FilesWithAndWithoutMeta_When_Created_Then_GuidsComeFromTheirMeta() {
    var meta = "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nTextScriptImporter:\n  userData: \n"u8.ToArray();
    using var ms = new MemoryStream();
    new UnityPackageFormatDescriptor().Create(ms, [
      ArchiveInputInfo.InMemory("Assets/a.txt", "alpha"u8.ToArray()),
      ArchiveInputInfo.InMemory("Assets/a.txt.meta", meta),
      ArchiveInputInfo.InMemory("b.txt", "beta"u8.ToArray()),
      new ArchiveInputInfo("Sub", "Sub", true),
    ], new FormatCreateOptions());
    var assets = FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(ms.ToArray()));
    Assert.That(assets.Select(a => a.Path), Is.EqualTo(new[] { "Assets/Sub", "Assets/a.txt", "Assets/b.txt" }));
    var a = assets.Single(x => x.Path == "Assets/a.txt");
    Assert.That((a.Guid, a.Meta), Is.EqualTo(("0123456789abcdef0123456789abcdef", meta)));
    var b = assets.Single(x => x.Path == "Assets/b.txt");
    Assert.That(Encoding.UTF8.GetString(b.Meta!), Is.EqualTo($"fileFormatVersion: 2\nguid: {b.Guid}\n"));
    var sub = assets.Single(x => x.Path == "Assets/Sub");
    Assert.That((sub.Asset, Encoding.UTF8.GetString(sub.Meta!)), Is.EqualTo(((byte[]?)null, $"fileFormatVersion: 2\nguid: {sub.Guid}\nfolderAsset: yes\n")));
  }

  [Test, Category("RoundTrip")]
  public void Given_UnityPackage_When_AssetReplacedAddedAndRemoved_Then_OthersKeepTheirGuids() {
    using var ms = new MemoryStream();
    ms.Write(Vector("morpeh-ScriptTemplates.unitypackage"));
    var d = new UnityPackageFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("Assets/ScriptTemplates/ECS__System.cs.txt", "replaced"u8.ToArray()),
      ArchiveInputInfo.InMemory("Assets/ScriptTemplates/New.cs.txt", "new"u8.ToArray())]);
    d.Remove(ms, ["Assets/ScriptTemplates/ECS__Provider.cs.txt"]);
    var assets = FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(ms.ToArray()));
    Assert.That(assets.Select(a => a.Path), Does.Not.Contain("Assets/ScriptTemplates/ECS__Provider.cs.txt"));
    var system = assets.Single(a => a.Path == "Assets/ScriptTemplates/ECS__System.cs.txt");
    Assert.That((system.Guid, system.Asset, Sha(system.Meta)), Is.EqualTo(("810e6d4c2f900744685a89d0777f668f", "replaced"u8.ToArray(),
      "c42d252b745dd06fa1a91aa732d1333335fb0514443347873f8fa36991bd6698")));
    Assert.That(assets.Single(a => a.Path == "Assets/ScriptTemplates/New.cs.txt").Asset, Is.EqualTo("new"u8.ToArray()));
    Assert.That(assets, Has.Count.EqualTo(9));
  }

  [Test, Category("EdgeCase")]
  public void Given_AssetWithoutPathname_When_Read_Then_InvalidData() {
    using var ms = new MemoryStream();
    using (var gz = new FileFormat.Gzip.GzipStream(ms, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
    using (var tar = new FileFormat.Tar.TarWriter(gz, leaveOpen: true)) {
      tar.AddEntry(new FileFormat.Tar.TarEntry { Name = "0123456789abcdef0123456789abcdef/asset", Size = 1 }, (ReadOnlySpan<byte>)[7]);
      tar.Finish();
    }
    Assert.Throws<InvalidDataException>(() => FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(ms.ToArray())));
  }

  [Test, Category("EdgeCase")]
  public void Given_NotGzip_When_Read_Then_Throws()
    => Assert.Catch<Exception>(() => FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream("not a package"u8.ToArray())));

  [Test, Category("EdgeCase")]
  public void Given_TruncatedPackage_When_Read_Then_Throws() {
    var bytes = Vector("morpeh-ScriptTemplates.unitypackage")[..800];
    Assert.Catch<Exception>(() => FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(bytes)));
  }

  [Test, Category("Boundary")]
  public void Given_NoInputs_When_Created_Then_EmptyPackage() {
    using var ms = new MemoryStream();
    new UnityPackageFormatDescriptor().Create(ms, [], new FormatCreateOptions());
    Assert.That(FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(ms.ToArray())), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Given_UnityPackagePath_When_Detected_Then_UnityPackageNotGzip() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_upkg_{Guid.NewGuid():N}.unitypackage");
    File.WriteAllBytes(path, Vector("morpeh-ScriptTemplates.unitypackage"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("UnityPackage"));
    } finally { File.Delete(path); }
  }

  // ── Python as the oracle for what we write ───────────────────────────

  private static string? Python() {
    foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : ["python3", "python"])
      foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0))
        if (File.Exists(Path.Combine(dir.Trim(), name))) return Path.Combine(dir.Trim(), name);
    return null;
  }

  private static (int Exit, string Out, string Err) Run(string exe, string args) {
    var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, stdout, stderr);
  }

  [Test, Category("EndToEnd")]
  public void Given_OurPackage_When_ReadByPythonTarfile_Then_SameAssets() {
    var python = Python();
    if (python == null) Assert.Ignore("python not on PATH.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_upkg_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.unitypackage");
      using (var fs = File.Create(path))
        new UnityPackageFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("Assets/Scripts/Hello.cs", "class Hello {}"u8.ToArray()),
          ArchiveInputInfo.InMemory("Assets/Textures/t.bin", [.. Enumerable.Range(0, 300).Select(i => (byte)i)]),
          new ArchiveInputInfo("Assets/Empty", "Assets/Empty", true)], new FormatCreateOptions());
      var script = Path.Combine(dir, "list.py");
      File.WriteAllText(script, ListingScript);
      var (exit, listing, errors) = Run(python, $"-I \"{script}\" \"{path}\"");
      Assert.That(exit, Is.Zero, errors);
      AssertMatchesListing(FileFormat.UnityPackage.UnityPackage.Read(new MemoryStream(File.ReadAllBytes(path))), listing);
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("EndToEnd")]
  public void Given_OurPackage_When_ExtractedByUnitypackageExtractor_Then_AssetsAtTheirPaths() {
    var python = Python();
    if (python == null) Assert.Ignore("python not on PATH.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_upkg_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      // CWB_PYTHONPATH names a `pip install --target` folder, for Pythons that ignore PYTHONPATH.
      var runner = Path.Combine(dir, "run.py");
      var extra = Environment.GetEnvironmentVariable("CWB_PYTHONPATH") is { Length: > 0 } p ? $"sys.path.insert(0, r'{p}')\n" : "";
      File.WriteAllText(runner, "import sys, runpy\n" + extra
        + "try:\n  import unitypackage_extractor\nexcept ImportError:\n  sys.exit(3)\n"
        + "if len(sys.argv) < 2:\n  sys.exit(0)\n"
        + "sys.argv = ['unitypackage_extractor'] + sys.argv[1:]\nrunpy.run_module('unitypackage_extractor', run_name='__main__')\n");
      if (Run(python, $"\"{runner}\"").Exit == 3) Assert.Ignore("unitypackage_extractor is not installed (or set CWB_PYTHONPATH).");
      var path = Path.Combine(dir, "ours.unitypackage");
      using (var fs = File.Create(path))
        new UnityPackageFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("Assets/Scripts/Hello.cs", "class Hello {}"u8.ToArray())],
          new FormatCreateOptions());
      var output = Path.Combine(dir, "out");
      Directory.CreateDirectory(output);
      var (exit, stdout, errors) = Run(python, $"\"{runner}\" \"{path}\" \"{output}\"");
      Assert.That(exit, Is.Zero, stdout + errors);
      Assert.That(File.ReadAllText(Path.Combine(output, "Assets", "Scripts", "Hello.cs")), Is.EqualTo("class Hello {}"));
    } finally { Directory.Delete(dir, true); }
  }
}
