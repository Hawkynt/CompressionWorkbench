using System.Diagnostics;
using Compression.Registry;
using FileFormat.Creg;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.StructuredPseudoArchives;

/// <summary>
/// Our CREG writer judged by an independent reader: libyal's libcreg (LGPL), through its
/// <c>pycreg</c> Python binding (<c>pip install libcreg-python</c>). libcreg must open what we
/// wrote without flagging it corrupted, and walking it must find exactly our keys, and under
/// each key exactly our values with their type and bytes. The same walk over the real
/// Windows 9x <c>USER.DAT</c> vector keeps the oracle itself honest. Ignored where no Python
/// with pycreg is on the PATH.
/// </summary>
[TestFixture]
[Category("ArchiveExternal")]
public sealed class CregLibcregOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_creg_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        if (Run(candidate, ["-c", "import pycreg"]).ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  });

  /// <summary>Prints "corrupted", then one line per key (K path) and value (V path type base64) in libcreg's order.</summary>
  private const string WalkScript = """
    import base64, gzip, sys, pycreg
    path = sys.argv[1]
    data = gzip.open(path).read() if path.endswith('.gz') else open(path, 'rb').read()
    import io
    f = pycreg.file()
    f.open_file_object(io.BytesIO(data))
    print('corrupted' if f.is_corrupted() else 'clean')
    def walk(key, prefix):
        for value in key.values:
            print('V', prefix + (value.name or ''), value.type, base64.b64encode(value.data or b'').decode())
        for sub in key.sub_keys:
            print('K', prefix + sub.name)
            walk(sub, prefix + sub.name + '/')
    walk(f.get_root_key(), '')
    """;

  private static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
      StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
    };
    start.Environment["PYTHONIOENCODING"] = "utf-8";
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }

  private string[] Walk(string path) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with pycreg on the PATH (`pip install libcreg-python`).");
    var (code, stdout, stderr) = Run(python!, ["-c", WalkScript, path]);
    Assert.That(code, Is.EqualTo(0), $"libcreg could not read {Path.GetFileName(path)}:\n{stderr}");
    return stdout.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
  }

  [Test, Category("HappyPath")]
  public void GivenTheRealWindows9xHive_WhenLibcregWalksIt_ThenItSeesTheSameEntryCountAsOurReader() {
    var gz = Path.Combine(this._tmpDir, "user.dat.gz");
    using (var resource = typeof(CregLibcregOracleTests).Assembly.GetManifestResourceStream("ReferenceVectors.windows9x-user.dat.gz")!)
    using (var file = File.Create(gz))
      resource.CopyTo(file);

    var lines = this.Walk(gz);
    Assert.Multiple(() => {
      Assert.That(lines[0], Is.EqualTo("clean"));
      Assert.That(lines.Length - 1, Is.EqualTo(2305), "keys plus values below the root, as our reader counts them");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenNestedKeysAndBinaryValues_WhenLibcregWalksOurHive_ThenItFindsExactlyThemUncorrupted() {
    var largest = Enumerable.Range(0, ushort.MaxValue).Select(static x => (byte)x).ToArray();
    var inputs = new ArchiveInputInfo[] {
      new("Software", "Software", IsDirectory: true),
      ArchiveInputInfo.InMemory("Software/Acme/Blob", new byte[] { 0, 1, 0x7f, 0x80, 0xff }),
      ArchiveInputInfo.InMemory("Software/Acme/Empty", ReadOnlySpan<byte>.Empty),
      ArchiveInputInfo.InMemory("Software/Acme/Sub/Deep", new byte[] { 0xa5 }),
      // 65,535 bytes: the 16-bit length field's maximum, and enough to spill into a second RGDB block.
      ArchiveInputInfo.InMemory("Zeta/Largest", largest),
      ArchiveInputInfo.InMemory("Zeta/Äpfel", new byte[] { 0x42 }),
    };
    var path = Path.Combine(this._tmpDir, "ours.dat");
    using (var output = File.Create(path))
      new CregFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    static string V(string p, byte[] d) => $"V {p} 3 {Convert.ToBase64String(d)}";
    var expected = new[] {
      "clean",
      "K Software",
      "K Software/Acme",
      V("Software/Acme/Blob", [0, 1, 0x7f, 0x80, 0xff]),
      V("Software/Acme/Empty", []),
      "K Software/Acme/Sub",
      V("Software/Acme/Sub/Deep", [0xa5]),
      "K Zeta",
      V("Zeta/Largest", largest),
      V("Zeta/Äpfel", [0x42]),
    };
    Assert.That(this.Walk(path), Is.EqualTo(expected));
  }
}
