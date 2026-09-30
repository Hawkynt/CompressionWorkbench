using Win32Exception = System.ComponentModel.Win32Exception;
using System.Diagnostics;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.PyInstaller;

namespace Compression.Tests.PyInstaller;

/// <summary>
/// Our CArchive (PKG) writer judged by PyInstaller itself: its own
/// <c>PyInstaller.archive.readers.CArchiveReader</c> must list and extract what we wrote, and
/// for stored entries its own <c>CArchiveWriter</c> must produce the very same bytes. Compressed
/// entries are compared by content only: CPython's zlib and .NET's deflate make different
/// (equally valid) streams. The tests run wherever a Python with PyInstaller installed is on the
/// PATH and are ignored elsewhere.
/// </summary>
[TestFixture]
[Category("ArchiveExternal")]
public sealed class PyInstallerCArchiveOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_pyi_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static readonly Lazy<string?> Python = new(FindPythonWithPyInstaller);

  private static string? FindPythonWithPyInstaller() {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        var (code, stdout, _) = Run(candidate, ["-c", "import PyInstaller.archive.readers, PyInstaller.archive.writers, sys; print(sys.version_info[0] * 100 + sys.version_info[1])"]);
        if (code == 0 && int.TryParse(stdout.Trim(), out _)) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  }

  private static string RequirePython() {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with PyInstaller on the PATH (`pip install pyinstaller`).");
    return python!;
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }

  private static int PythonVersion(string python) => int.Parse(Run(python, ["-c", "import sys; print(sys.version_info[0] * 100 + sys.version_info[1])"]).StdOut.Trim());

  /// <summary>Lists name, typecode, compression flag and the SHA-256 of each extracted entry.</summary>
  private const string ReaderScript = """
    import hashlib, sys
    from PyInstaller.archive.readers import CArchiveReader
    archive = CArchiveReader(sys.argv[1])
    for name in sorted(archive.toc):
        offset, length, uncompressed, flag, typecode = archive.toc[name]
        data = archive.extract(name)
        assert len(data) == uncompressed, name
        print(f"{name}\t{typecode}\t{flag}\t{hashlib.sha256(data).hexdigest()}")
    """;

  private const string WriterScript = """
    import sys
    from PyInstaller.archive.writers import CArchiveWriter
    out, libname, typecode = sys.argv[1], sys.argv[2], sys.argv[3]
    names = sys.argv[4::2]
    sources = sys.argv[5::2]
    CArchiveWriter(out, [(n, s, False, typecode) for n, s in zip(names, sources)], libname)
    """;

  private static readonly (string Name, byte[] Data)[] Payload = [
    ("readme.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("PyInstaller CArchive payload ", 64)))),
    ("empty.bin", []),
    // 18-byte entry header + 13 name bytes + NUL = 32: already aligned, so no padding.
    ("exactly13.bin", [1, 2, 3]),
    ("assets/nested/deeper.dat", Enumerable.Range(0, 70_000).Select(static i => (byte)(i * 31)).ToArray()),
  ];

  private byte[] WriteOurs(string method, int version, string libraryName, string targetOs = "posix") {
    using var output = new MemoryStream();
    new PyInstallerFormatDescriptor().Create(output,
      [.. Payload.Select(static p => ArchiveInputInfo.InMemory(p.Name, p.Data))],
      new FormatCreateOptions(method) {
        FormatSpecific = new(StringComparer.OrdinalIgnoreCase) {
          ["PythonVersion"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
          ["PythonLibraryName"] = libraryName,
          ["TypeCode"] = "x",
          ["TargetOs"] = targetOs,
        },
      });
    return output.ToArray();
  }

  [TestCase("zlib", 1, TestName = "GivenZlibEntries_WhenPyInstallersReaderOpensOurPkg_ThenEveryEntryExtractsIdentically")]
  [TestCase("stored", 0, TestName = "GivenStoredEntries_WhenPyInstallersReaderOpensOurPkg_ThenEveryEntryExtractsIdentically")]
  public void PyInstallerReader_ExtractsWhatWeWrote(string method, int expectedFlag) {
    var python = RequirePython();
    var path = Path.Combine(this._tmpDir, "ours.pkg");
    File.WriteAllBytes(path, this.WriteOurs(method, PythonVersion(python), "libpython3.13.so.1.0"));

    var (code, stdout, stderr) = Run(python, ["-c", ReaderScript, path]);
    Assert.That(code, Is.EqualTo(0), $"PyInstaller's CArchiveReader rejected our PKG:\n{stderr}");

    var expected = Payload
      .OrderBy(static p => p.Name, StringComparer.Ordinal)
      .Select(p => $"{p.Name}\tx\t{expectedFlag}\t{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(p.Data))}");
    Assert.That(stdout.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'), Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void GivenStoredEntries_WhenPyInstallersOwnWriterBuildsTheSamePkg_ThenTheBytesAreIdentical() {
    var python = RequirePython();
    var version = PythonVersion(python);
    const string libraryName = "python313.dll";
    var arguments = new List<string> { "-c", WriterScript, Path.Combine(this._tmpDir, "reference.pkg"), libraryName, "x" };
    foreach (var (name, data) in Payload) {
      var source = Path.Combine(this._tmpDir, "src_" + arguments.Count);
      File.WriteAllBytes(source, data);
      arguments.Add(name);
      arguments.Add(source);
    }

    var (code, _, stderr) = Run(python, arguments);
    Assert.That(code, Is.EqualTo(0), $"PyInstaller's CArchiveWriter failed:\n{stderr}");

    var reference = File.ReadAllBytes(Path.Combine(this._tmpDir, "reference.pkg"));
    Assert.That(this.WriteOurs("stored", version, libraryName, OperatingSystem.IsWindows() ? "windows" : "posix"), Is.EqualTo(reference),
      "a stored PKG must be byte-identical to the one PyInstaller writes for the same entries");
  }

  [Test, Category("HappyPath")]
  public void GivenABarePkgWithoutAnExtension_WhenDetecting_ThenTheTrailingCookieIdentifiesPyInstaller() {
    var path = Path.Combine(this._tmpDir, "PKG-00");
    File.WriteAllBytes(path, this.WriteOurs("zlib", 313, "python313.dll"));
    Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.PyInstaller));
  }
}
