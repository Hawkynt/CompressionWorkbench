using System.Diagnostics;
using System.Text;
using Compression.Registry;
using FileFormat.Json;
using FileFormat.MessagePack;
using FileFormat.Pickle;
using FileFormat.Reg;
using FileFormat.Storable;
using FileFormat.Xml;

namespace Compression.Tests.StructuredPseudoArchives;

/// <summary>
/// Hands what our writers produce to the real third-party reader, live, on a host that has it.
///
/// This is the same question <c>StructuredPseudoArchiveReferenceVectorTests</c> answers from frozen
/// bytes, asked again against the tool itself. It is advisory — <c>ci.yml</c> filters
/// <c>ExternalInterop</c> out of the gating step, and a runner without CPython or Perl must not
/// fail the build — so it supplements the gate rather than being it. Where the gate pins a byte
/// sequence a tool once accepted, this fixture is what re-establishes that the tool still does.
///
/// The registry case writes to the live registry. It stays inside a throwaway key under
/// <c>HKCU\Software\</c>, is deleted again in a finally block, and never touches HKLM or any key
/// Windows itself owns.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class StructuredPseudoArchiveExternalToolTests {
  private const string RegistryImportKey = @"HKCU\Software\CompressionWorkbench\ExternalToolRoundTrip";

  [Test]
  public void PythonMsgpackReadsWhatWeWrote() => AssertPythonRecovers(
    new MessagePackFormatDescriptor(),
    "msgpack",
    "import msgpack; value = msgpack.unpackb(raw, raw=False)");

  [Test]
  public void PythonPickleReadsWhatWeWrote() => AssertPythonRecovers(
    new PickleFormatDescriptor(),
    "pickle",
    // Loading is safe here because the payload is our own, built from dicts and bytes only. Nothing
    // in CompressionWorkbench ever calls pickle.loads; that is the entire point of our opcode reader.
    "import pickle; value = pickle.loads(raw)");

  [Test]
  public void PythonJsonReadsWhatWeWrote() => AssertPythonRecovers(
    new JsonFormatDescriptor(),
    "json",
    """
    import base64, json
    def unwrap(node):
        if isinstance(node, dict) and node.get('$cwb:type') == 'binary':
            return base64.b64decode(node['$cwb:data'])
        return {k: unwrap(v) for k, v in node.items()}
    value = unwrap(json.loads(raw.decode('utf-8')))
    """);

  [Test]
  public void PythonElementTreeReadsWhatWeWrote() => AssertPythonRecovers(
    new XmlFormatDescriptor(),
    "xml.etree",
    """
    import base64, xml.etree.ElementTree as ET
    NS = '{urn:hawkynt:compressionworkbench:structured-archive:1}'
    def walk(element):
        result = {}
        for child in element:
            name = child.get('name')
            if child.tag == NS + 'file':
                result[name] = base64.b64decode(child.text or '')
            else:
                result[name] = walk(child)
        return result
    value = walk(ET.fromstring(raw))
    """);

  [Test]
  public void PerlStorableReadsWhatWeWrote() {
    var perl = Which("perl");
    if (perl is null) {
      Assert.Ignore("perl is not installed, so Storable cannot be asked what it makes of our output.");
      return;
    }

    var path = WriteTemporary(".storable", ReferenceVectorFixture.Create(new StorableFormatDescriptor()));
    try {
      // `retrieve` reconstructs plain data only; the payload is hashes and scalars, and our writer
      // never emits a blessed, tied or code reference for Storable to revive.
      const string script =
        "use strict; use warnings; use Storable qw(retrieve);" +
        "my $v = retrieve($ARGV[0]);" +
        "printf('%s|%s|%s', unpack('H*', $v->{dir}{'file.bin'}), unpack('H*', $v->{dir}{'long.bin'}), unpack('H*', $v->{'top.bin'}));";

      var (exit, output) = Run(perl, ["-e", script, path]);
      Assert.That(exit, Is.Zero, $"perl Storable::retrieve failed on our output:{Environment.NewLine}{output}");
      Assert.That(output.Trim(), Is.EqualTo(ExpectedPayloadDigest()),
        "Perl read our Storable file and recovered different bytes");
    } finally {
      TryDelete(path);
    }
  }

  /// <summary>
  /// The direction only Windows can answer: does <c>reg.exe</c> accept a <c>.reg</c> file we wrote?
  /// It has to import it, and the key it then holds has to carry our three payloads -- read back out
  /// of an export <c>reg.exe</c> itself produced, so neither half of the comparison is ours.
  ///
  /// What this deliberately does not assert is byte equality between our file and reg.exe's
  /// re-export. That is a claim about formatting, it is already pinned against a frozen export in
  /// the gating tier, and hanging it on whichever Windows build a runner happens to be would make
  /// this fixture fail for a reason that is not about our code.
  /// </summary>
  [Test]
  public void RegExeImportsWhatWeWrote() {
    if (!OperatingSystem.IsWindows()) {
      Assert.Ignore("reg.exe is a Windows tool.");
      return;
    }

    var produced = ReferenceVectorFixture.Create(new RegFormatDescriptor());
    // Our writer always emits the same fixed archive root; retarget it at a throwaway key so the
    // import cannot disturb anything a user keeps.
    var text = Encoding.Unicode.GetString(produced, 2, produced.Length - 2)
      .Replace(@"HKEY_CURRENT_USER\Software\CompressionWorkbench\PseudoArchive",
               @"HKEY_CURRENT_USER\Software\CompressionWorkbench\ExternalToolRoundTrip",
               StringComparison.Ordinal);
    var scratch = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

    var importPath = WriteTemporary(".reg", scratch);
    var exportPath = Path.Combine(Path.GetTempPath(), $"cwb-reg-{Guid.NewGuid():N}.reg");
    try {
      RunReg("delete", RegistryImportKey, "/f");
      var (importExit, importOutput) = RunReg("import", importPath);
      Assert.That(importExit, Is.Zero, $"reg.exe refused the .reg file we wrote:{Environment.NewLine}{importOutput}");

      var (exportExit, exportOutput) = RunReg("export", RegistryImportKey, exportPath, "/y");
      Assert.That(exportExit, Is.Zero, $"reg.exe could not export the key it had just imported:{Environment.NewLine}{exportOutput}");

      var descriptor = new RegFormatDescriptor();
      var exported = File.ReadAllBytes(exportPath);
      const string root = "HKEY_CURRENT_USER/Software/CompressionWorkbench/ExternalToolRoundTrip";
      Assert.Multiple(() => {
        Assert.That(ReferenceVectorFixture.Extract(descriptor, exported, $"{root}/dir/file.bin"),
          Is.EqualTo(ReferenceVectorFixture.FileBin));
        Assert.That(ReferenceVectorFixture.Extract(descriptor, exported, $"{root}/dir/long.bin"),
          Is.EqualTo(ReferenceVectorFixture.LongBin));
        Assert.That(ReferenceVectorFixture.Extract(descriptor, exported, $"{root}/top.bin"),
          Is.EqualTo(ReferenceVectorFixture.TopBin));
      });
    } finally {
      RunReg("delete", RegistryImportKey, "/f");
      TryDelete(importPath);
      TryDelete(exportPath);
    }
  }

  // -------------------------------------------------------------------------------------- plumbing

  /// <summary>The three payloads of <see cref="ReferenceVectorFixture.StructuredArchiveInputs"/>,
  /// hex-joined, so every language prints its recovered value in one comparable form.</summary>
  private static string ExpectedPayloadDigest() => string.Join('|',
    Convert.ToHexString(ReferenceVectorFixture.FileBin),
    Convert.ToHexString(ReferenceVectorFixture.LongBin),
    Convert.ToHexString(ReferenceVectorFixture.TopBin)).ToLowerInvariant();

  private static void AssertPythonRecovers(IArchiveCreatable descriptor, string module, string decode) {
    var python = WhichPython();
    if (python is null) {
      Assert.Ignore($"CPython is not installed, so {module} cannot be asked what it makes of our output.");
      return;
    }

    var path = WriteTemporary(".bin", ReferenceVectorFixture.Create(descriptor));
    try {
      var script = $"""
        import sys
        raw = open(sys.argv[1], 'rb').read()
        {decode}
        print('|'.join(x.hex() for x in (value['dir']['file.bin'], value['dir']['long.bin'], value['top.bin'])))
        """;

      var (exit, output) = Run(python, ["-c", script, path]);
      Assert.That(exit, Is.Zero, $"CPython {module} failed on our output:{Environment.NewLine}{output}");
      Assert.That(output.Trim(), Is.EqualTo(ExpectedPayloadDigest()),
        $"CPython {module} read our output and recovered different bytes");
    } finally {
      TryDelete(path);
    }
  }

  private static string WriteTemporary(string extension, byte[] content) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-vector-{Guid.NewGuid():N}{extension}");
    File.WriteAllBytes(path, content);
    return path;
  }

  private static void TryDelete(string path) {
    try { File.Delete(path); } catch { /* best effort */ }
  }

  private static string? WhichPython() => Which("python3") ?? Which("python");

  private static string? Which(string tool) {
    var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : [tool];
    var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
      .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var directory in directories)
      foreach (var name in names) {
        string candidate;
        try { candidate = Path.Combine(directory, name); } catch (ArgumentException) { continue; }
        if (File.Exists(candidate)) return candidate;
      }

    return null;
  }

  private static (int Exit, string Output) RunReg(params string[] arguments)
    => Run("reg.exe", arguments);

  private static (int Exit, string Output) Run(string tool, IReadOnlyList<string> arguments) {
    var start = new ProcessStartInfo(tool) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit(120_000);
    return (process.HasExited ? process.ExitCode : -1, output);
  }
}
