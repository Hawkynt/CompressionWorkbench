using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Compression.Lib;
using FileFormat.PyInstaller;

namespace Compression.Tests.PyInstaller;

/// <summary>
///   Acceptance gate for <see cref="PyInstallerFormatDescriptor"/> and
///   <see cref="PyInstallerReader"/>.
///
///   <para>
///     A hand-built CArchive fixture pins the documented onefile layout — trailing
///     <c>MEI</c> cookie, the <c>!8sIIii64s</c> header, big-endian TOC entries
///     (<c>entryLen/dataPos/dataLen/uncomprLen/cflag/typecode/name</c>), zlib
///     inflation on <c>cflag==1</c>, and the embedded PYZ module TOC (Python
///     marshal). When Python + PyInstaller are on PATH, a second test performs a
///     genuine round-trip: it builds a real onefile executable and re-extracts it.
///   </para>
/// </summary>
[TestFixture]
public class PyInstallerTests {

  // ── Marshal / PYZ builders ─────────────────────────────────────────────────

  private const byte FlagRef = 0x80;

  /// <summary>
  ///   Serializes a PYZ table of contents — a marshalled list of
  ///   <c>(name, (typecode, offset, length))</c> tuples — exactly as CPython's
  ///   <c>marshal.dump</c> emits it (interned short-ASCII names, ref-flagged ints).
  /// </summary>
  private static byte[] BuildPyzToc(IReadOnlyList<string> modules) {
    var ms = new MemoryStream();
    ms.WriteByte((byte)('[' | FlagRef)); // TYPE_LIST | FLAG_REF
    WriteInt32Le(ms, modules.Count);
    foreach (var module in modules) {
      ms.WriteByte((byte)')'); // TYPE_SMALL_TUPLE
      ms.WriteByte(2);
      // name: TYPE_SHORT_ASCII | FLAG_REF
      var name = Encoding.ASCII.GetBytes(module);
      ms.WriteByte((byte)('z' | FlagRef));
      ms.WriteByte((byte)name.Length);
      ms.Write(name);
      // (typecode, offset, length)
      ms.WriteByte((byte)')');
      ms.WriteByte(3);
      WriteMarshalInt(ms, 0, true);   // typecode 0 (module)
      WriteMarshalInt(ms, 17, true);  // offset
      WriteMarshalInt(ms, 1365, false); // length
    }
    return ms.ToArray();
  }

  private static void WriteMarshalInt(Stream s, int value, bool refFlag) {
    s.WriteByte((byte)('i' | (refFlag ? FlagRef : 0))); // TYPE_INT
    WriteInt32Le(s, value);
  }

  /// <summary>Wraps a marshalled TOC in the PYZ container header (magic, py-magic, u32 BE TOC offset).</summary>
  private static byte[] BuildPyz(IReadOnlyList<string> modules) {
    var toc = BuildPyzToc(modules);
    var ms = new MemoryStream();
    ms.Write("PYZ\0"u8);
    ms.Write([0xF3, 0x0D, 0x0D, 0x0A]); // arbitrary python bytecode magic
    WriteUInt32Be(ms, 12);              // TOC offset: immediately after the 12-byte header
    ms.Write(toc);
    return ms.ToArray();
  }

  // ── CArchive builder ────────────────────────────────────────────────────────

  private sealed record FixtureEntry(char TypeCode, string Name, byte[] Stored, int UncompressedLen, bool Compressed);

  /// <summary>
  ///   Assembles a full onefile image: a PE-like prefix, the CArchive data blobs,
  ///   the big-endian TOC, and the trailing MEI cookie. Offsets are computed
  ///   relative to the CArchive start so the reader's
  ///   <c>archiveStart = cookiePos + cookieSize - packageLength</c> math is exercised.
  /// </summary>
  private static byte[] BuildImage(IReadOnlyList<FixtureEntry> entries, int prefixLen = 64) {
    var image = new MemoryStream();
    // PE-like prefix so detection sees an "MZ" file with an overlay.
    var prefix = new byte[prefixLen];
    prefix[0] = (byte)'M';
    prefix[1] = (byte)'Z';
    image.Write(prefix);

    var archiveStart = image.Position;

    // Data region: lay each entry's stored bytes out sequentially.
    var dataPositions = new long[entries.Count];
    for (var i = 0; i < entries.Count; i++) {
      dataPositions[i] = image.Position - archiveStart;
      image.Write(entries[i].Stored);
    }

    // TOC region.
    var tocPos = image.Position - archiveStart;
    var toc = new MemoryStream();
    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      var nameBytes = Encoding.UTF8.GetBytes(e.Name);
      var entryLen = 18 + nameBytes.Length;
      WriteUInt32Be(toc, (uint)entryLen);
      WriteUInt32Be(toc, (uint)dataPositions[i]);
      WriteUInt32Be(toc, (uint)e.Stored.Length);
      WriteUInt32Be(toc, (uint)e.UncompressedLen);
      toc.WriteByte((byte)(e.Compressed ? 1 : 0));
      toc.WriteByte((byte)e.TypeCode);
      toc.Write(nameBytes);
    }
    var tocBytes = toc.ToArray();
    image.Write(tocBytes);

    // Cookie: !8sIIii64s
    var packageLength = tocPos + tocBytes.Length + PyInstallerReader.CookieSize;
    image.Write(PyInstallerReader.MagicCookie);
    WriteUInt32Be(image, (uint)packageLength);
    WriteUInt32Be(image, (uint)tocPos);
    WriteUInt32Be(image, (uint)tocBytes.Length);
    WriteUInt32Be(image, 313); // Python 3.13
    var libName = new byte[64];
    Encoding.ASCII.GetBytes("python313.dll").CopyTo(libName, 0);
    image.Write(libName);

    return image.ToArray();
  }

  private static byte[] ZlibCompress(byte[] data) {
    var ms = new MemoryStream();
    using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      zs.Write(data);
    return ms.ToArray();
  }

  private static void WriteInt32Le(Stream s, int value) {
    Span<byte> b = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, value);
    s.Write(b);
  }

  private static void WriteUInt32Be(Stream s, uint value) {
    Span<byte> b = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, value);
    s.Write(b);
  }

  private static IReadOnlyList<FixtureEntry> SampleEntries(out byte[] moduleBody, out byte[] pyzBlob) {
    moduleBody = Encoding.UTF8.GetBytes("this is a fake code object body for a bootstrap module");
    pyzBlob = BuildPyz(["alpha", "beta", "gamma"]);
    return [
      new FixtureEntry('b', "hello.txt", "Hello, world"u8.ToArray(), 12, Compressed: false),
      new FixtureEntry('m', "bootstrap", ZlibCompress(moduleBody), moduleBody.Length, Compressed: true),
      new FixtureEntry('z', "PYZ.pyz", pyzBlob, pyzBlob.Length, Compressed: false),
    ];
  }

  // ── Tests: hand-built fixture (no external tooling) ─────────────────────────

  [Category("HappyPath")]
  [Test]
  public void FindCookie_LocatesTrailingMei() {
    var image = BuildImage(SampleEntries(out _, out _));
    using var ms = new MemoryStream(image);
    var pos = PyInstallerReader.FindCookie(ms);
    Assert.That(pos, Is.GreaterThanOrEqualTo(0));
    // The 8 magic bytes must actually be at the reported position.
    ms.Position = pos;
    var got = new byte[PyInstallerReader.MagicCookie.Length];
    ms.ReadExactly(got);
    Assert.That(got, Is.EqualTo(PyInstallerReader.MagicCookie));
  }

  [Category("HappyPath")]
  [Test]
  public void Header_ParsesPythonVersionAndLibName() {
    var image = BuildImage(SampleEntries(out _, out _));
    using var ms = new MemoryStream(image);
    var reader = new PyInstallerReader(ms);
    Assert.Multiple(() => {
      Assert.That(reader.PythonVersion, Is.EqualTo(313));
      Assert.That(reader.PythonLibraryName, Is.EqualTo("python313.dll"));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void ReadToc_ReturnsAllEntriesWithMetadata() {
    var image = BuildImage(SampleEntries(out _, out _));
    using var ms = new MemoryStream(image);
    var reader = new PyInstallerReader(ms);
    var toc = reader.ReadToc();

    Assert.That(toc, Has.Count.EqualTo(3));
    Assert.Multiple(() => {
      Assert.That(toc[0].Name, Is.EqualTo("hello.txt"));
      Assert.That(toc[0].TypeCode, Is.EqualTo('b'));
      Assert.That(toc[0].IsCompressed, Is.False);
      Assert.That(toc[1].Name, Is.EqualTo("bootstrap"));
      Assert.That(toc[1].IsCompressed, Is.True);
      Assert.That(toc[2].TypeCode, Is.EqualTo('z'));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void GetData_InflatesCompressedEntryToOriginalBytes() {
    var image = BuildImage(SampleEntries(out var moduleBody, out _));
    using var ms = new MemoryStream(image);
    var reader = new PyInstallerReader(ms);
    var toc = reader.ReadToc();

    var stored = reader.GetData(toc[0]);
    var inflated = reader.GetData(toc[1]);
    Assert.Multiple(() => {
      Assert.That(stored, Is.EqualTo("Hello, world"u8.ToArray()));
      Assert.That(inflated, Is.EqualTo(moduleBody));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void GetPyzModuleNames_EnumeratesEmbeddedModules() {
    var image = BuildImage(SampleEntries(out _, out _));
    using var ms = new MemoryStream(image);
    var reader = new PyInstallerReader(ms);
    var pyz = reader.ReadToc().Single(e => e.TypeCode == 'z');

    var modules = reader.GetPyzModuleNames(pyz);
    Assert.That(modules, Is.EqualTo(new[] { "alpha", "beta", "gamma" }));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_IncludesEntriesAndPyzModules() {
    var image = BuildImage(SampleEntries(out _, out _));
    using var ms = new MemoryStream(image);
    var descriptor = new PyInstallerFormatDescriptor();
    var listing = descriptor.List(ms, null);

    var names = listing.Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("metadata.json"));
      Assert.That(names, Does.Contain("diagnostics.json"));
      Assert.That(names, Does.Contain("original_packed.bin"));
      Assert.That(names, Does.Contain("hello.txt"));
      Assert.That(names, Does.Contain("bootstrap"));
      Assert.That(names, Does.Contain("PYZ.pyz"));
      Assert.That(names, Does.Contain("PYZ.pyz/alpha"));
      Assert.That(names, Does.Contain("PYZ.pyz/gamma"));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesDecompressedEntries() {
    var image = BuildImage(SampleEntries(out var moduleBody, out var pyzBlob));
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_pyi_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      using var ms = new MemoryStream(image);
      new PyInstallerFormatDescriptor().Extract(ms, outDir, null, null);

      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "hello.txt")), Is.EqualTo("Hello, world"u8.ToArray()));
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "bootstrap")), Is.EqualTo(moduleBody));
        // The PYZ container is written verbatim (its contents are not re-expanded).
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "PYZ.pyz")), Is.EqualTo(pyzBlob));
        Assert.That(File.ReadAllText(Path.Combine(outDir, "metadata.json")), Does.Contain("\"packer\": \"pyinstaller\""));
        Assert.That(File.ReadAllText(Path.Combine(outDir, "diagnostics.json")), Does.Contain("\"canRebuildExecutable\": false"));
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "original_packed.bin")), Is.EqualTo(image));
      });
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Category("Exceptional")]
  [Test]
  public void Constructor_WithoutCookie_Throws() {
    using var ms = new MemoryStream(new byte[512]);
    Assert.Throws<InvalidDataException>(() => _ = new PyInstallerReader(ms));
  }

  [Category("HappyPath")]
  [Test]
  public void Detection_MeiCookie_ResolvesToPyInstaller() {
    var image = BuildImage(SampleEntries(out _, out _));
    var path = Path.Combine(Path.GetTempPath(), "cwb_pyi_" + Guid.NewGuid().ToString("N")[..8] + ".exe");
    try {
      File.WriteAllBytes(path, image);
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.PyInstaller));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── Test: real PyInstaller round-trip (gated on Python + PyInstaller) ────────

  [Category("Integration")]
  [Test]
  public void RealOnefile_BuildThenExtract_RoundTrips() {
    var python = FindPython();
    if (python == null || !HasPyInstaller(python))
      Assert.Ignore("Python + PyInstaller not available on PATH — skipping real round-trip.");

    var work = Path.Combine(Path.GetTempPath(), "cwb_pyi_rt_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var script = Path.Combine(work, "app.py");
      File.WriteAllText(script,
        "def greet(name):\n    return 'Hi ' + name\n\n" +
        "if __name__ == '__main__':\n    print(greet('there'))\n");

      var dist = Path.Combine(work, "dist");
      var build = Path.Combine(work, "build");
      var exit = RunProcess(python!, [
        "-m", "PyInstaller", "--onefile", "--noconfirm",
        "--distpath", dist, "--workpath", build, "--specpath", work, script
      ], work, TimeSpan.FromMinutes(4), out var stdout, out var stderr);

      var exe = Path.Combine(dist, "app.exe");
      if (exit != 0 || !File.Exists(exe))
        Assert.Ignore($"PyInstaller build did not produce an exe (exit {exit}).\n{stdout}\n{stderr}");

      // Detection must recognise the real onefile executable.
      Assert.That(FormatDetector.Detect(exe), Is.EqualTo(FormatDetector.Format.PyInstaller));

      using var fs = File.OpenRead(exe);
      var descriptor = new PyInstallerFormatDescriptor();
      var listing = descriptor.List(fs, null);

      // A real onefile build always carries a PYZ plus a python shared library.
      Assert.Multiple(() => {
        Assert.That(listing.Any(e => e.Name.EndsWith(".pyz")), Is.True, "expected a PYZ entry");
        Assert.That(listing.Any(e => e.Kind == "PYZ module"), Is.True, "expected PYZ module children");
        Assert.That(listing.Any(e => e.Name.Contains("python", StringComparison.OrdinalIgnoreCase)),
          Is.True, "expected a python shared library binary");
      });

      fs.Position = 0;
      var outDir = Path.Combine(work, "extract");
      descriptor.Extract(fs, outDir, null, null);
      Assert.That(Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Any(),
        Is.True, "extraction produced no files");
    } finally {
      try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* best-effort */ }
    }
  }

  // ── Process helpers ─────────────────────────────────────────────────────────

  private static string? FindPython() {
    foreach (var candidate in new[] { "python", "python3", "py" }) {
      try {
        var exit = RunProcess(candidate, ["--version"], null, TimeSpan.FromSeconds(20), out var so, out var se);
        if (exit == 0 && (so + se).Contains("Python 3", StringComparison.Ordinal))
          return candidate;
      } catch {
        /* candidate not launchable */
      }
    }
    return null;
  }

  private static bool HasPyInstaller(string python) {
    try {
      return RunProcess(python, ["-m", "PyInstaller", "--version"], null, TimeSpan.FromSeconds(30), out _, out _) == 0;
    } catch {
      return false;
    }
  }

  private static int RunProcess(string fileName, string[] args, string? workingDir, TimeSpan timeout,
      out string stdout, out string stderr) {
    var psi = new ProcessStartInfo {
      FileName = fileName,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    if (workingDir != null) psi.WorkingDirectory = workingDir;

    using var proc = Process.Start(psi)!;
    var outTask = proc.StandardOutput.ReadToEndAsync();
    var errTask = proc.StandardError.ReadToEndAsync();
    if (!proc.WaitForExit((int)timeout.TotalMilliseconds)) {
      try { proc.Kill(true); } catch { /* ignore */ }
      stdout = outTask.GetAwaiter().GetResult();
      stderr = errTask.GetAwaiter().GetResult();
      return -1;
    }
    stdout = outTask.GetAwaiter().GetResult();
    stderr = errTask.GetAwaiter().GetResult();
    return proc.ExitCode;
  }
}
