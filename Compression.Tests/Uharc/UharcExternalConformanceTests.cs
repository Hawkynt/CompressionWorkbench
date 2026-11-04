using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Analysis.ExternalTools;
using FileFormat.Uharc;

namespace Compression.Tests.Uharc;

/// <summary>
/// UHARC 0.6b's own <c>uharc.exe</c> (Win32 console; found on the PATH or through
/// <c>CWB_TOOL_UHARC</c>) as the oracle: it packs inputs we generate in every mode we decode,
/// and our reader must give back the inputs byte for byte. A second gate reads a directory of
/// real-world archives (<c>CWB_UHARC_SAMPLES</c>), whose licences we cannot vouch for and so do
/// not commit, and requires every file of every decodable one to pass its CRC.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class UharcExternalConformanceTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-uharc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static string RequireUharc() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("uharc.exe is a Win32 console program.");
    var path = ToolDiscovery.GetToolPath("uharc");
    if (path == null)
      Assert.Ignore("uharc.exe (UHARC 0.6b) not found: put it on the PATH or point CWB_TOOL_UHARC at it.");
    return path!;
  }

  private static void Run(string tool, string workingDirectory, params string[] arguments) {
    var info = new ProcessStartInfo(tool) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var argument in arguments)
      info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(300_000)) {
      process.Kill(entireProcessTree: true);
      Assert.Fail("uharc.exe timed out.");
    }

    Assert.That(process.ExitCode, Is.Zero, output.Result + error.Result);
  }

  private static Dictionary<string, byte[]> WriteInputs(string directory) {
    var random = new Random(20051001);
    var words = "alpha beta gamma delta epsilon zeta eta theta iota kappa".Split(' ');
    var text = string.Concat(Enumerable.Range(0, 30000).Select(i => words[random.Next(words.Length)] + (i % 11 == 0 ? "\r\n" : " ")));
    var noise = new byte[20000];
    random.NextBytes(noise);
    var wave = Enumerable.Range(0, 30000).SelectMany(i => BitConverter.GetBytes((short)(Math.Sin(i * 0.013) * 20000))).ToArray();
    var image = Enumerable.Range(0, 40000).Select(i => (byte)(i % 3 == 0 ? i / 120 : i % 3 == 1 ? i / 7 : 200 - i / 300)).ToArray();
    var stub = UharcTests.MinimalPe(0x200, 0x2000);
    for (var i = 0x200; i < stub.Length - 5; i += 9)
      stub[i] = 0xE8;

    var inputs = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["text.txt"] = System.Text.Encoding.ASCII.GetBytes(text),
      ["noise.bin"] = noise,
      ["wave.pcm"] = wave,
      ["image.raw"] = image,
      ["sub/program.exe"] = stub,
      ["sub/empty.dat"] = [],
      ["one.bin"] = [42],
    };
    foreach (var (name, data) in inputs) {
      var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }

    return inputs;
  }

  [TestCase("-m0")]
  [TestCase("-m1")]
  [TestCase("-m2")]
  [TestCase("-m3")]
  [TestCase("-mz")]
  [TestCase("-m2", "-md1")]
  [TestCase("-m3", "-md32768")]
  [TestCase("-mz", "-md64")]
  [TestCase("-m2", "-mm-")]
  [TestCase("-mz", "-mm-")]
  [TestCase("-m2", "-pwsecret")]
  public void GivenInputsUharcPacked_WhenOurReaderExtracts_ThenEveryFileIsByteIdentical(params string[] switches) {
    var uharc = RequireUharc();
    var input = Path.Combine(this._tmp, "in");
    Directory.CreateDirectory(input);
    var expected = WriteInputs(input);
    var archive = Path.Combine(this._tmp, "test.uha");
    Run(uharc, input, ["a", "-y", "-d0", "-r", .. switches, archive, "*"]);

    var password = switches.FirstOrDefault(s => s.StartsWith("-pw", StringComparison.Ordinal))?[3..];
    using var stream = File.OpenRead(archive);
    var reader = new UharcReader(stream, password);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.FileName).Order(), Is.EqualTo(expected.Keys.Order()));
    foreach (var entry in files)
      Assert.That(reader.Extract(entry), Is.EqualTo(expected[entry.FileName]), entry.FileName);
  }

  [Test]
  public void GivenRealWorldArchives_WhenExtracted_ThenEveryDecodableFilePassesItsCrc() {
    var directory = Environment.GetEnvironmentVariable("CWB_UHARC_SAMPLES");
    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
      Assert.Ignore("Set CWB_UHARC_SAMPLES to a directory of real .uha archives (e.g. the dexvert archive/uharc samples).");

    var checkedFiles = 0;
    foreach (var path in Directory.EnumerateFiles(directory)) {
      var head = new byte[4];
      using (var probe = File.OpenRead(path))
        if (probe.Read(head) < 4 || !UharcHeader.HasMagic(head))
          continue;

      using var stream = File.OpenRead(path);
      var reader = new UharcReader(stream);
      if (!reader.CanDecodeMethod)
        continue;
      foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
        Assert.That(() => reader.Extract(entry), Throws.Nothing, $"{Path.GetFileName(path)}: {entry.FileName}");
        ++checkedFiles;
      }
    }

    Assert.That(checkedFiles, Is.Positive, "no decodable UHARC 0.6 archive in the sample directory");
  }
}
