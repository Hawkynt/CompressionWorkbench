using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using Compression.Tests.CodecProof;
using FileFormat.NuFx;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.NuFx;

/// <summary>
/// Third-party proof for the ShrinkIt LZW codec (<c>BB_NuLzw</c>) and the NuFX archive around it.
/// <list type="bullet">
/// <item>Real archives (CiderPress II test corpus, see <c>NuFx/ReferenceVectors/README.md</c>):
/// every data fork, LZW/1 and LZW/2, decodes to the bytes CiderPress II 1.2.0 extracts.</item>
/// <item>Where <c>cp2</c> (CiderPress II's command-line tool) is on the PATH or named by
/// <c>CWB_TOOL_CP2</c>, it tests and extracts the LZW/1 and LZW/2 archives we write.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class NuFxCiderPressOracleTests {

  [TestCase("old-archive.shk", "README", 489, "b5debc463f74b05665b15e20e15e333fb977639b1b430a8893014907434cd7c7")]
  [TestCase("old-archive.shk", "ChangeLog", 7711, "5f7d8f5d21313042f9a73f39dee520d190147b76c02a46ecfb37a076dc138b8d")]
  [TestCase("old-archive.shk", "nulib.doc", 21237, "4fba25c6bd785c8649c5daf619d4f1388b65a7ca0200a0619f33d25e713a5476")]
  [TestCase("PatchHFS.shk", "patchhfs/PatchHFS.c", 1730, "b0b1b7fdebbf60c66310a19afcd4aa7c5c9c32b34cb7f8453ccc66c19b34aff1")]
  [TestCase("PatchHFS.shk", "patchhfs/PatchHFS.Doc", 3679, "396f35cc8e1ba7be4dde82bf888e61306ac85fec3f06df79b7c5298ebd074082")]
  [TestCase("PatchHFS.shk", "patchhfs/Finder.Data", 150, "9e72100349037128b12a019d07ce6126d0e49aee825516d6baf325171b0efe77")]
  [TestCase("PatchHFS.shk", "patchhfs/mkpatch", 91, "d4d7d649b1be83fe143ecd9e87597d42ddd0243b2e62d2f9eae959734849b489")]
  [TestCase("PatchHFS.shk", "patchhfs/PatchHFS", 11253, "cf7d857a3567b6542c968857f3629fc1b90b5889d7da6151a582d34abb56117b")]
  [Category("HappyPath")]
  public void GivenARealArchive_WhenADataForkIsExtracted_ThenItIsWhatCiderPressExtracts(string archive, string entry, int length, string sha256) {
    var data = new NuFxFormatDescriptor().ExtractEntryToMemory(new MemoryStream(Resource(archive)), entry, null);
    Assert.Multiple(() => {
      Assert.That(data, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(sha256));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheLzw1Archive_WhenListed_ThenEveryThreadIsLzw1() {
    var entries = new NuFxFormatDescriptor().List(new MemoryStream(Resource("old-archive.shk")), null);
    Assert.That(entries.Select(e => e.Method), Is.All.EqualTo("NuLZW/1"));
  }

  [TestCase("nulzw1", TestName = "GivenOurLzw1Archive_WhenCiderPressTestsAndExtractsIt_ThenEveryForkMatches")]
  [TestCase("nulzw2", TestName = "GivenOurLzw2Archive_WhenCiderPressTestsAndExtractsIt_ThenEveryForkMatches")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void CiderPressReadsOurArchive(string method) {
    var cp2 = FindCp2();
    if (cp2 is null) Assert.Ignore("CiderPress II's cp2 is not on the PATH (or set CWB_TOOL_CP2).");
    var files = new Dictionary<string, byte[]> {
      ["TEXT.BIN"] = CodecProofData.Text(24_000),
      ["MIXED.BIN"] = CodecProofData.Mixed(6_000),
      ["ZEROS.BIN"] = new byte[70_000],
    };
    var dir = Path.Combine(Path.GetTempPath(), "cwb-nufx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var archive = Path.Combine(dir, "ours.shk");
      using (var output = File.Create(archive))
        new NuFxFormatDescriptor().Create(output, [.. files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value))], new FormatCreateOptions { MethodName = method });

      Assert.That(Run(cp2!, dir, "test", archive), Does.Contain("no failures"));
      Run(cp2!, dir, "extract", archive);
      foreach (var (name, content) in files)
        Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(content), name);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"NuFxVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource NuFxVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static string? FindCp2() {
    var configured = Environment.GetEnvironmentVariable("CWB_TOOL_CP2");
    if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
      return configured;
    try {
      Run("cp2", Path.GetTempPath(), "version");
      return "cp2";
    } catch (Win32Exception) {
      return null;
    }
  }

  private static string Run(string tool, string workingDirectory, params string[] arguments) {
    var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.EqualTo(0), $"cp2 {string.Join(' ', arguments)} failed:\n{stderr}");
    return stdout.Result;
  }
}
