using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Analysis.ExternalTools;
using Compression.Registry;
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
  [TestCase("-mr")]
  [TestCase("-mw")]
  [TestCase("-mw", "-md1")]
  [TestCase("-mx")]
  [TestCase("-mx", "-md1")]
  [TestCase("-mx", "-md64")]
  [TestCase("-mx", "-mm-")]
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

  [TestCase(null)]
  [TestCase("store")]
  [TestCase("rle")]
  [TestCase("lz78")]
  [TestCase("alz")]
  [TestCase("ppm")]
  public void GivenOurArchive_WhenUharcExtracts_ThenEveryFileIsByteIdentical(string? method) {
    var uharc = RequireUharc();
    var archive = Path.Combine(this._tmp, "ours.uha");
    var inputs = UharcWriterTests.SampleInputs();
    File.WriteAllBytes(archive, UharcWriterTests.Create(method, inputs));
    var output = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(output);
    Run(uharc, output, "x", "-y", "-t" + output, archive);

    foreach (var input in inputs) {
      var path = Path.Combine(output, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
      if (input.IsDirectory)
        Assert.That(Directory.Exists(path), Is.True, input.ArchiveName);
      else
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
    }
  }

  private static void AssertExtracted(string output, IEnumerable<ArchiveInputInfo> inputs) {
    foreach (var input in inputs) {
      var path = Path.Combine(output, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
      if (input.IsDirectory)
        Assert.That(Directory.Exists(path), Is.True, input.ArchiveName);
      else
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
    }
  }

  [TestCase(null)]
  [TestCase("store")]
  [TestCase("rle")]
  [TestCase("lz78")]
  [TestCase("alz")]
  [TestCase("ppm")]
  public void GivenOurPasswordArchive_WhenUharcExtractsWithThePassword_ThenEveryFileIsByteIdentical(string? method) {
    var uharc = RequireUharc();
    var archive = Path.Combine(this._tmp, "ours.uha");
    var inputs = UharcWriterTests.SampleInputs();
    File.WriteAllBytes(archive, UharcWriterTests.CreateWith(new FormatCreateOptions(method) { Password = "Open Sesame 42!" }, inputs));
    var output = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(output);
    Run(uharc, output, "x", "-y", "-pwOpen Sesame 42!", "-t" + output, archive);
    AssertExtracted(output, inputs);
  }

  /// <summary>
  /// UHARC's own Win32 stub (<c>UHARCSFX.EXE</c> from <c>uharc06b.zip</c>, beside uharc.exe) in
  /// front of our archive: the self-extractor itself must extract it.
  /// </summary>
  [TestCase(null)]
  [TestCase("secret")]
  public void GivenOurArchiveBehindUharcsSfxStub_WhenTheSelfExtractorRuns_ThenEveryFileIsByteIdentical(string? password) {
    var uharc = RequireUharc();
    var stub = Path.Combine(Path.GetDirectoryName(uharc)!, "UHARCSFX.EXE");
    if (!File.Exists(stub))
      Assert.Ignore("UHARCSFX.EXE (from uharc06b.zip) is not beside uharc.exe.");

    var sfx = Path.Combine(this._tmp, "ours.exe");
    var inputs = UharcWriterTests.SampleInputs();
    File.WriteAllBytes(sfx, UharcWriterTests.CreateWith(new FormatCreateOptions {
      Password = password,
      FormatSpecific = { [UharcFormatDescriptor.SfxStubOption] = stub },
    }, inputs));
    var output = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(output);
    string[] arguments = password == null ? ["x", "-y", "-t" + output] : ["x", "-y", "-pw" + password, "-t" + output];
    Run(sfx, output, arguments);
    AssertExtracted(output, inputs);
  }

  /// <summary>
  /// UHARC 0.4 BETA's <c>UHARC04.EXE</c> (28 Dec 2001, Win32; <c>CWB_TOOL_UHARC04</c> names it)
  /// extracts the 0.4 archives we write.
  /// </summary>
  [TestCase("store", 0L, null)]
  [TestCase("rle", 0L, null)]
  [TestCase("lz78", 0L, null)]
  [TestCase("lzp", 0L, null)]
  [TestCase("lzp", 1024L, "secret")]
  [TestCase("lz78", 0L, "secret")]
  [TestCase("alz", 0L, null)]
  [TestCase("alz", 1024L, "secret")]
  public void GivenOurVersion04Archive_WhenUharc04Extracts_ThenEveryFileIsByteIdentical(string method, long dictionary, string? password) {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("UHARC04.EXE is a Win32 console program.");
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_UHARC04");
    if (string.IsNullOrEmpty(tool) || !File.Exists(tool))
      Assert.Ignore("Set CWB_TOOL_UHARC04 to UHARC 0.4 BETA's UHARC04.EXE.");

    var random = new Random(2001);
    var noise = new byte[150000];
    random.NextBytes(noise);
    var inputs = UharcWriterTests.SampleInputs().Append(ArchiveInputInfo.InMemory("noise.dat", noise))
      .Append(ArchiveInputInfo.InMemory("fill.txt", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 40000).Select(i => $"{i * 7919 % 100003:x} "))))).ToList();
    var archive = Path.Combine(this._tmp, "ours.uha");
    File.WriteAllBytes(archive, UharcWriterTests.CreateWith(new FormatCreateOptions(method) {
      Password = password,
      DictSize = dictionary,
      FormatSpecific = { [UharcFormatDescriptor.VersionOption] = "4" },
    }, inputs));
    var output = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(output);
    string[] arguments = password == null ? ["x", "-y", archive] : ["x", "-y", "-pw" + password, archive];
    Run(tool!, output, arguments);
    AssertExtracted(output, inputs);
  }

  /// <summary>
  /// UHARC 0.3 BETA's <c>uharc_u.exe</c> (14 Jun 2000, Win32; <c>CWB_TOOL_UHARC03</c> names it),
  /// which prints nothing, extracts the 0.3 archives we write; the files are compared.
  /// </summary>
  [TestCase("store", 0L, null)]
  [TestCase("rle", 0L, null)]
  [TestCase("lz78", 0L, null)]
  [TestCase("lzp", 0L, null)]
  [TestCase("lzp", 1024L, "secret")]
  [TestCase("alz", 0L, null)]
  [TestCase("alz", 1024L, "secret")]
  public void GivenOurVersion03Archive_WhenUharc03Extracts_ThenEveryFileIsByteIdentical(string method, long dictionary, string? password) {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("uharc_u.exe is a Win32 console program.");
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_UHARC03");
    if (string.IsNullOrEmpty(tool) || !File.Exists(tool))
      Assert.Ignore("Set CWB_TOOL_UHARC03 to UHARC 0.3 BETA's uharc_u.exe.");

    var random = new Random(2000);
    var noise = new byte[150000];
    random.NextBytes(noise);
    var inputs = UharcWriterTests.SampleInputs().Append(ArchiveInputInfo.InMemory("noise.dat", noise))
      .Append(ArchiveInputInfo.InMemory("fill.txt", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 40000).Select(i => $"{i * 7919 % 100003:x} "))))).ToList();
    var archive = Path.Combine(this._tmp, "ours.uha");
    File.WriteAllBytes(archive, UharcWriterTests.CreateWith(new FormatCreateOptions(method) {
      Password = password,
      DictSize = dictionary,
      FormatSpecific = { [UharcFormatDescriptor.VersionOption] = "3" },
    }, inputs));
    var output = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(output);
    string[] arguments = password == null ? ["x", "-y", archive] : ["x", "-y", "-pw" + password, archive];
    Run(tool!, output, arguments);
    AssertExtracted(output, inputs);
  }

  /// <summary>
  /// UHARC 0.2 (21 Dec 1997, a DOS program; <c>CWB_TOOL_UHARC02</c> names its <c>UHARC.EXE</c>)
  /// under DOSBox (<c>CWB_DOSBOX</c>, a dosbox-x or dosbox executable) extracts the 0.2 archives
  /// we write; the files are compared. The executable exercises the 0.2 executable filter.
  /// </summary>
  [TestCase("store", 0L)]
  [TestCase("lz78", 0L)]
  [TestCase("alz", 0L)]
  [TestCase("alz", 1024L)]
  public void GivenOurVersion02Archive_WhenUharc02ExtractsItInDosbox_ThenEveryFileIsByteIdentical(string method, long dictionary) {
    var dosbox = Environment.GetEnvironmentVariable("CWB_DOSBOX");
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_UHARC02");
    if (string.IsNullOrEmpty(dosbox) || !File.Exists(dosbox))
      Assert.Ignore("Set CWB_DOSBOX to a dosbox-x or dosbox executable.");
    if (string.IsNullOrEmpty(tool) || !File.Exists(tool))
      Assert.Ignore("Set CWB_TOOL_UHARC02 to UHARC 0.2's UHARC.EXE.");

    var random = new Random(1997);
    var noise = new byte[60000];
    random.NextBytes(noise);
    var program = UharcTests.MinimalPe(0x200, 0x2000);
    for (var i = 0x200; i < program.Length - 5; i += 7) {
      program[i] = 0xE8;
      BitConverter.TryWriteBytes(program.AsSpan(i + 1), i % 3 == 0 ? -i : i * 3 % 9000 - 4000);
    }

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("README.TXT", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i % 37} of the 0.2 sample\r\n")))),
      ArchiveInputInfo.InMemory("NOISE.BIN", noise),
      ArchiveInputInfo.InMemory("EMPTY.DAT", []),
      ArchiveInputInfo.InMemory("SUB/PROGRAM.EXE", program),
      ArchiveInputInfo.InMemory("SUB/ZEROS.BIN", new byte[40000]),
    };
    File.WriteAllBytes(Path.Combine(this._tmp, "OURS.UHA"), UharcWriterTests.CreateWith(new FormatCreateOptions(method) {
      DictSize = dictionary,
      FormatSpecific = { [UharcFormatDescriptor.VersionOption] = "2" },
    }, inputs));
    File.Copy(tool!, Path.Combine(this._tmp, "UHARC.EXE"));
    Directory.CreateDirectory(Path.Combine(this._tmp, "OUT"));
    File.WriteAllText(Path.Combine(this._tmp, "GO.BAT"), "@echo off\r\ncd OUT\r\n..\\UHARC x -y ..\\OURS.UHA > ..\\X.LOG\r\ncd ..\r\n", System.Text.Encoding.ASCII);
    Run(dosbox!, this._tmp, "-silent", "-nolog", "-fastlaunch", "-c", $"mount c \"{this._tmp}\"", "-c", "c:", "-c", "call GO.BAT", "-c", "exit");

    foreach (var input in inputs)
      Assert.That(File.ReadAllBytes(Path.Combine(this._tmp, "OUT", input.ArchiveName.Replace('/', Path.DirectorySeparatorChar))),
        Is.EqualTo(input.InMemoryContent), input.ArchiveName);
  }

  /// <summary>Enough varied data to fill LZ78's 32768 phrases several times over.</summary>
  [TestCase("rle", 0)]
  [TestCase("lz78", 0)]
  [TestCase("alz", 0)]
  [TestCase("alz", 1024)]
  [TestCase("lzp", 1024)]
  [TestCase("ppm", 0)]
  [TestCase("ppm", 1024)]
  public void GivenALargeMixedInput_WhenWeWriteIt_ThenUharcExtractsIt(string method, long dictionary) {
    var uharc = RequireUharc();
    var random = new Random(2005);
    var words = Enumerable.Range(0, 3000).Select(i => new string((char)('a' + i % 26), 1 + i % 7) + i.ToString("x")).ToArray();
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 120000).Select(i => words[random.Next(words.Length)] + (i % 13 == 0 ? "\r\n" : " "))));
    var noise = new byte[300000];
    random.NextBytes(noise);
    var runs = Enumerable.Range(0, 2000).SelectMany(i => Enumerable.Repeat((byte)(0xE0 + i % 32), i % 300)).ToArray();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("text.txt", text),
      ArchiveInputInfo.InMemory("noise.bin", noise),
      ArchiveInputInfo.InMemory("runs.bin", runs),
    };
    var archive = Path.Combine(this._tmp, "large.uha");
    File.WriteAllBytes(archive, UharcWriterTests.CreateWith(new FormatCreateOptions(method) { DictSize = dictionary }, inputs));
    var output = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(output);
    Run(uharc, output, "x", "-y", "-t" + output, archive);

    foreach (var input in inputs)
      Assert.That(File.ReadAllBytes(Path.Combine(output, input.ArchiveName)), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
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
        if (probe.Read(head) < 4 || head is not [(byte)'U', (byte)'H', (byte)'A', 2 or 3 or 4 or 6])
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

    Assert.That(checkedFiles, Is.Positive, "no decodable UHARC archive in the sample directory");
  }

  /// <summary>
  /// UHARC codes two files of the Canterbury corpus specially: <c>kennedy.xls</c> with its own
  /// PPM model (filters bit 6) and <c>ptt5</c> as a transposed fax page (bit 7). The corpus is not
  /// ours to commit, so <c>CWB_UHARC_CORPUS</c> points at a directory holding it.
  /// </summary>
  [TestCase("-mx")]
  [TestCase("-m2")]
  [TestCase("-mz")]
  public void GivenCanterburyCorpusFiles_WhenUharcPacksThem_ThenOurReaderExtractsThem(string mode) {
    var uharc = RequireUharc();
    var corpus = Environment.GetEnvironmentVariable("CWB_UHARC_CORPUS");
    string[] names = ["alice29.txt", "ptt5", "kennedy.xls"];
    if (string.IsNullOrEmpty(corpus) || names.Any(n => !File.Exists(Path.Combine(corpus, n))))
      Assert.Ignore("Set CWB_UHARC_CORPUS to a directory holding the Canterbury corpus (alice29.txt, ptt5, kennedy.xls).");

    var input = Path.Combine(this._tmp, "in");
    Directory.CreateDirectory(input);
    foreach (var name in names)
      File.Copy(Path.Combine(corpus!, name), Path.Combine(input, name));
    var archive = Path.Combine(this._tmp, "corpus.uha");
    Run(uharc, input, ["a", "-y", "-d0", mode, archive, .. names]);

    using var stream = File.OpenRead(archive);
    var reader = new UharcReader(stream);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      Assert.That(reader.Extract(entry), Is.EqualTo(File.ReadAllBytes(Path.Combine(input, entry.FileName))), entry.FileName);
  }

  [TestCase(587984, 2988578)]
  [TestCase(1566200, 2934336)]
  [TestCase(2934336, 438144)]
  [TestCase(3706306, 1498414)]
  public void GivenLargerFileSizeRulePairs_WhenUharcWritesPpm_ThenEveryByteMatches(int previousSize, int currentSize) {
    var tool = RequireUharc();
    var input = Path.Combine(this._tmp, "in");
    Directory.CreateDirectory(input);
    var expected = new Dictionary<string, byte[]> {
      ["a.txt"] = SizedText(previousSize),
      ["b.txt"] = SizedText(currentSize),
    };
    foreach (var (name, bytes) in expected)
      File.WriteAllBytes(Path.Combine(input, name), bytes);
    var archive = Path.Combine(this._tmp, "sizes.uha");
    Run(tool, input, ["a", "-y", "-d0", "-mx", archive, "a.txt", "b.txt"]);

    using var stream = File.OpenRead(archive);
    var reader = new UharcReader(stream);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToArray();
    Assert.That(files.Select(e => e.FileName), Is.EquivalentTo(expected.Keys));
    foreach (var entry in files)
      Assert.That(reader.Extract(entry), Is.EqualTo(expected[entry.FileName]), entry.FileName);
  }

  private static byte[] SizedText(int size) {
    var words = "model order context symbol escape record table window range coder stream archive header block reset point size file text binary byte value count total".Split(' ');
    var random = new Random(size);
    var bytes = new byte[size];
    for (var position = 0; position < size;) {
      var line = string.Join(' ', Enumerable.Range(0, 8).Select(k => words[(position / 97 + k * 7) % words.Length]))
        + (random.NextDouble() < 0.97 ? ".\r\n" : "!\r\n");
      var encoded = System.Text.Encoding.ASCII.GetBytes(line);
      var count = Math.Min(encoded.Length, size - position);
      encoded.AsSpan(0, count).CopyTo(bytes.AsSpan(position));
      position += count;
    }
    return bytes;
  }
}
