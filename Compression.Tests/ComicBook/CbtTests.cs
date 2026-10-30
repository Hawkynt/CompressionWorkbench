using System.Diagnostics;
using Compression.Analysis.ExternalTools;
using Compression.Registry;
using Compression.Lib;

namespace Compression.Tests.ComicBook;

/// <summary>
/// .cbt comic book archives: an extension-only view of the tar container. Oracle: 7-Zip reads
/// what we write and writes what we read.
/// </summary>
[TestFixture]
public class CbtTests {

  private static readonly byte[] Page1 = "first comic page bytes"u8.ToArray();
  private static readonly byte[] Page2 = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251))];
  private static readonly byte[] Page3 = "third page, added later"u8.ToArray();

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_cbx_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, true); } catch { /* best effort */ }
  }

  private static MemoryStream Create(IArchiveCreatable d) {
    var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("page01.png", Page1), ArchiveInputInfo.InMemory("page02.png", Page2)],
      new FormatCreateOptions());
    ms.Position = 0;
    return ms;
  }

  private static IEnumerable<IFormatDescriptor> Descriptors() {
    yield return new FileFormat.Cbt.CbtFormatDescriptor();
  }

  [TestCaseSource(nameof(Descriptors)), Category("HappyPath")]
  public void Given_Descriptor_When_Inspected_Then_ExtensionOnlyAndWritable(IFormatDescriptor d) {
    Assert.That(d.MagicSignatures, Is.Empty, "byte-identical to the inner container: extension-only detection");
    Assert.That(d.Extensions, Has.Count.EqualTo(1));
    Assert.That(d.DefaultExtension, Is.EqualTo(d.Extensions[0]));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Given_Registry_When_ResolvedByExtension_Then_ComicDescriptorsWin() {
    FormatRegistration.EnsureInitialized();
    Assert.That(FormatRegistry.GetByExtension(".cbt")?.Id, Is.EqualTo("Cbt"));
  }

  [TestCaseSource(nameof(Descriptors)), Category("RoundTrip")]
  public void Given_Archive_When_CreatedAddedRemoved_Then_SurvivorsByteIdentical(IFormatDescriptor d) {
    using var ms = Create((IArchiveCreatable)d);
    var ops = (IArchiveFormatOperations)d;
    Assert.That(ops.List(ms, null).Select(e => e.Name), Is.EquivalentTo(new[] { "page01.png", "page02.png" }));

    ms.Position = 0;
    ((IArchiveModifiable)d).Add(ms, [ArchiveInputInfo.InMemory("page03.png", Page3)]);
    ms.Position = 0;
    ((IArchiveModifiable)d).Remove(ms, ["page01.png"]);
    ms.Position = 0;
    Assert.That(ops.List(ms, null).Select(e => e.Name), Is.EquivalentTo(new[] { "page02.png", "page03.png" }));
    ms.Position = 0;
    Assert.That(ops.ExtractEntryToMemory(ms, "page02.png", null), Is.EqualTo(Page2));
    ms.Position = 0;
    Assert.That(ops.ExtractEntryToMemory(ms, "page03.png", null), Is.EqualTo(Page3));
  }

  [TestCaseSource(nameof(Descriptors)), Category("EdgeCase")]
  public void Given_Garbage_When_Listed_Then_ThrowsOrEmptyButNeverWrongBytes(IFormatDescriptor d) {
    using var ms = new MemoryStream([.. Enumerable.Repeat((byte)0x5A, 700)]);
    try {
      var entries = ((IArchiveFormatOperations)d).List(ms, null);
      Assert.That(entries.Where(e => e.OriginalSize > 0), Is.Empty);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or ArgumentException or FormatException) {
      Assert.Pass("rejected: " + ex.GetType().Name);
    }
  }

  private static (int Exit, string Out) Run(string exe, string args) {
    var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit(60_000);
    return (p.ExitCode, stdout.Result + stderr.Result);
  }

  [Test, Category("EndToEnd")]
  public void Given_OurCbt_When_SevenZipExtracts_Then_PagesByteIdentical() {
    var sz = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sz == null) Assert.Ignore("7-Zip not found.");
    var path = Path.Combine(this._tmp, "ours.cbt");
    using (var ms = Create(new FileFormat.Cbt.CbtFormatDescriptor()))
      File.WriteAllBytes(path, ms.ToArray());
    var outDir = Path.Combine(this._tmp, "x");
    var (exit, output) = Run(sz!, $"x -ttar \"{path}\" -o\"{outDir}\" -y");
    Assert.That(exit, Is.Zero, output);
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "page01.png")), Is.EqualTo(Page1));
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "page02.png")), Is.EqualTo(Page2));
  }

  [Test, Category("EndToEnd")]
  public void Given_SevenZipCbt_When_WeExtract_Then_PagesByteIdentical() {
    var sz = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sz == null) Assert.Ignore("7-Zip not found.");
    var src = Path.Combine(this._tmp, "src");
    Directory.CreateDirectory(src);
    File.WriteAllBytes(Path.Combine(src, "page01.png"), Page1);
    File.WriteAllBytes(Path.Combine(src, "page02.png"), Page2);
    var path = Path.Combine(this._tmp, "theirs.cbt");
    var (exit, output) = Run(sz!, $"a -ttar \"{path}\" \"{Path.Combine(src, "*")}\"");
    Assert.That(exit, Is.Zero, output);
    var d = new FileFormat.Cbt.CbtFormatDescriptor();
    using var fs = File.OpenRead(path);
    Assert.That(((IArchiveFormatOperations)d).ExtractEntryToMemory(fs, "page02.png", null), Is.EqualTo(Page2));
  }
}
