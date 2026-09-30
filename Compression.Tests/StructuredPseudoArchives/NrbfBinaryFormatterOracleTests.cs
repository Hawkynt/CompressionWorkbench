using System.Diagnostics;
using System.Text;
using Compression.Registry;
using FileFormat.Nrbf;

namespace Compression.Tests.StructuredPseudoArchives;

/// <summary>
/// Our MS-NRBF writer judged by the producer that defined the format: .NET Framework's
/// <c>BinaryFormatter</c>, still shipped with Windows PowerShell 5.1. It must deserialize what
/// we wrote into plain strings, byte arrays and nulls, and serializing that object graph again
/// must give back our bytes exactly. Runs on Windows; ignored where Windows PowerShell is absent.
/// </summary>
[TestFixture]
[Category("ArchiveExternal")]
public sealed class NrbfBinaryFormatterOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_nrbf_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static string RequireWindowsPowerShell() {
    if (!OperatingSystem.IsWindows()) Assert.Ignore("BinaryFormatter is exercised through Windows PowerShell 5.1.");
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    if (!File.Exists(path)) Assert.Ignore("Windows PowerShell 5.1 is not installed.");
    return path;
  }

  /// <summary>Deserializes $args[0], describes every element, and serializes the graph again to $args[1].</summary>
  private const string Script = """
    $ErrorActionPreference = 'Stop'
    $formatter = New-Object System.Runtime.Serialization.Formatters.Binary.BinaryFormatter
    $in = [System.IO.File]::OpenRead($args[0])
    try { $graph = $formatter.Deserialize($in) } finally { $in.Dispose() }
    foreach ($item in $graph) {
      if ($null -eq $item) { 'null' }
      elseif ($item -is [string]) { 'string ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($item)) }
      elseif ($item -is [byte[]]) { 'bytes ' + [Convert]::ToBase64String($item) }
      else { 'other ' + $item.GetType().FullName }
    }
    $out = [System.IO.File]::Create($args[1])
    try { $formatter.Serialize($out, $graph) } finally { $out.Dispose() }
    """;

  private static readonly ArchiveInputInfo[] Inputs = [
    ArchiveInputInfo.InMemory("dir/file.bin", [0x00, 0x01, 0x7f, 0x80, 0xff, 0x0a]),
    new("empty", "empty", IsDirectory: true),
    ArchiveInputInfo.InMemory("zero.bin", []),
    // A name longer than 127 UTF-8 bytes takes a two-byte length prefix; the umlaut is two bytes.
    ArchiveInputInfo.InMemory("nested/" + new string('n', 130) + "-ü.txt", Encoding.UTF8.GetBytes("umlaut")),
    ArchiveInputInfo.InMemory("large.bin", Enumerable.Range(0, 70_000).Select(static i => (byte)(i * 7)).ToArray()),
  ];

  private static byte[] Ours() {
    using var output = new MemoryStream();
    new NrbfFormatDescriptor().Create(output, Inputs, new FormatCreateOptions());
    return output.ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenOurArchive_WhenBinaryFormatterDeserializesAndReserializesIt_ThenTheBytesComeBackIdentical() {
    var powershell = RequireWindowsPowerShell();
    var ours = Ours();
    var input = Path.Combine(this._tmpDir, "ours.nrbf");
    var output = Path.Combine(this._tmpDir, "binaryformatter.nrbf");
    File.WriteAllBytes(input, ours);
    var scriptPath = Path.Combine(this._tmpDir, "oracle.ps1");
    File.WriteAllText(scriptPath, Script);

    var start = new ProcessStartInfo(powershell) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, input, output })
      start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.EqualTo(0), $"BinaryFormatter rejected our NRBF:\n{stderr}");

    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    var expected = new List<string> { "string " + B64(NrbfWriter.Marker) };
    foreach (var entry in Inputs.OrderBy(static i => i.ArchiveName, StringComparer.Ordinal)) {
      expected.Add("string " + B64(entry.ArchiveName));
      expected.Add(entry.IsDirectory ? "null" : "bytes " + Convert.ToBase64String(entry.ReadContent()));
    }
    Assert.Multiple(() => {
      Assert.That(stdout.Result.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'), Is.EqualTo(expected),
        "BinaryFormatter must see exactly our strings, byte arrays and nulls");
      Assert.That(File.ReadAllBytes(output), Is.EqualTo(ours),
        "BinaryFormatter must serialize the same graph to the same bytes");
    });
  }
}
