using System.Text;
using FileFormat.Shar;

namespace Compression.Tests.Shar;

/// <summary>
/// Shell archives against the shell: GNU sharutils' <c>shar</c> writes an archive in each of its
/// modes and <c>sh</c> extracts it, and our reader must list exactly the files <c>sh</c> made, byte
/// for byte; our writer's archives must extract under <c>sh</c> and <c>unshar</c> to the input.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("Wsl")]
public class SharGnuInteropTests {

  /// <summary>Text, text without a final newline, CRLF text, delimiter look-alikes, binary, empty, a subdirectory.</summary>
  private static readonly (string Name, byte[] Data)[] Files = [
    ("nl.txt", "hello\nworld\n"u8.ToArray()),
    ("nonl.txt", "no newline at end"u8.ToArray()),
    ("crlf.txt", "dos\r\nlines\r\n"u8.ToArray()),
    ("tricky.txt", "SHAR_EOF\nXstarts with X\n$HOME `x` \\ back\n"u8.ToArray()),
    ("bin.dat", [0, 1, 2, 0xFF, 0x80, 10, 13, 0x90, 0x60, 0x20]),
    ("empty.txt", []),
    ("sub/it's spaced.txt", "in sub\n"u8.ToArray()),
  ];

  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("shar") || !FsInteropToolbox.WslHasTool("uudecode"))
      Assert.Ignore("GNU sharutils (shar, uudecode) not reachable in WSL");
    this._dir = Directory.CreateTempSubdirectory("cwb_shar_").FullName;
  }

  [TearDown]
  public void TearDown() {
    if (this._dir != null && Directory.Exists(this._dir))
      Directory.Delete(this._dir, recursive: true);
  }

  private string W(string path) => FsInteropToolbox.WinToWsl(path);

  private string WriteInputs() {
    var input = Path.Combine(this._dir, "in");
    foreach (var (name, data) in Files) {
      var path = Path.Combine(input, name);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
    return input;
  }

  /// <summary>Runs <c>sh</c> on the archive in an empty directory and returns what it created.</summary>
  private Dictionary<string, byte[]> ExtractWithSh(string archive, string tool = "sh") {
    var output = Path.Combine(this._dir, "out-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(output);
    var command = tool == "sh"
      ? $"cd {W(output)} && sh {W(archive)} >/dev/null 2>&1"
      : $"unshar -d {W(output)} {W(archive)} >/dev/null 2>&1";
    var r = FsInteropToolbox.RunWsl(command, TimeSpan.FromMinutes(2));
    Assert.That(r.ExitCode, Is.Zero, $"{tool} failed: {r.StdErr}");
    return Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
      .ToDictionary(f => Path.GetRelativePath(output, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
  }

  [TestCase("")]
  [TestCase("-T")]
  [TestCase("-B")]
  [TestCase("-z")]
  [TestCase("-C xz")]
  [TestCase("-C bzip2")]
  [TestCase("-Q -m -x")]
  [TestCase("--no-md5-digest")]
  public void Given_GnuSharArchive_When_Read_Then_EntriesAreWhatShExtracts(string options) {
    var input = this.WriteInputs();
    var archive = Path.Combine(this._dir, "gnu.shar");
    // In its compressing modes shar 4.15.2 names the temporary file after the member's directory
    // ("begin 600 sub/_sh123/gzi"), which uudecode cannot create, so sh never extracts members in
    // subdirectories; those modes are compared on top-level files only.
    var compressing = options.Contains("-z", StringComparison.Ordinal) || options.Contains("-C", StringComparison.Ordinal);
    var members = Files.Where(f => !compressing || !f.Name.Contains('/')).ToList();
    var names = string.Join(' ', members.Select(f => "'" + f.Name.Replace("'", "'\\''") + "'"));
    var r = FsInteropToolbox.RunWsl($"cd {W(input)} && shar {options} {names} > {W(archive)}", TimeSpan.FromMinutes(2));
    Assert.That(r.ExitCode, Is.Zero, r.StdErr);

    var expected = this.ExtractWithSh(archive);
    Assert.That(expected.Keys, Is.EquivalentTo(members.Select(f => f.Name)), "sh extracts every file");

    using var stream = File.OpenRead(archive);
    var ours = new SharReader(stream).Entries.ToDictionary(e => e.FileName, e => e.Data, StringComparer.Ordinal);
    Assert.That(ours.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (name, data) in expected)
      Assert.That(ours[name], Is.EqualTo(data), name);
  }

  [TestCase("sh")]
  [TestCase("unshar")]
  public void Given_OurArchive_When_ExtractedByTheShell_Then_FilesAreTheInput(string tool) {
    var writer = new SharWriter();
    foreach (var (name, data) in Files)
      writer.AddFile(name, data);
    writer.AddFile("ümlaut.txt", Encoding.UTF8.GetBytes("non-ASCII name\n"));
    var archive = Path.Combine(this._dir, "ours.shar");
    File.WriteAllBytes(archive, writer.ToByteArray());

    var extracted = this.ExtractWithSh(archive, tool);
    Assert.That(extracted.Keys, Is.EquivalentTo(Files.Select(f => f.Name).Append("ümlaut.txt")));
    foreach (var (name, data) in Files)
      Assert.That(extracted[name], Is.EqualTo(data), name);
  }
}
