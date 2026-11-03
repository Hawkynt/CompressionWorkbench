using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// Names in ZIPs without the UTF-8 flag: from Windows Explorer (code page 437), from macOS,
/// Linux tools and phones (UTF-8 without the flag), and with an Info-ZIP Unicode Path field.
/// They used to be read as Latin-1, so "Ürlaub Straße.jpg" showed as "\x9arlaub Straáe.jpg".
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser c41d96d, 6fcaf63.</remarks>
[TestFixture]
public sealed class ZipNameEncodingTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_zipname_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  /// <summary>A stored ZIP written byte by byte, without the UTF-8 flag.</summary>
  private string Zip(string file, params (byte[] Name, byte[]? Extra, string Content)[] entries) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    var central = new MemoryStream();
    using var cw = new BinaryWriter(central);
    foreach (var (name, extra, content) in entries) {
      var data = Encoding.ASCII.GetBytes(content);
      var crc = Compression.Core.Checksums.Crc32.Compute(data);
      var offset = (uint)ms.Position;
      var ex = extra ?? [];
      w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
      w.Write((ushort)0); w.Write((ushort)0x21); w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length);
      w.Write((ushort)name.Length); w.Write((ushort)ex.Length); w.Write(name); w.Write(ex); w.Write(data);
      cw.Write(0x02014b50u); cw.Write((ushort)20); cw.Write((ushort)20); cw.Write((ushort)0); cw.Write((ushort)0);
      cw.Write((ushort)0); cw.Write((ushort)0x21); cw.Write(crc); cw.Write((uint)data.Length); cw.Write((uint)data.Length);
      cw.Write((ushort)name.Length); cw.Write((ushort)ex.Length); cw.Write((ushort)0); cw.Write((ushort)0);
      cw.Write((ushort)0); cw.Write(0x20u); cw.Write(offset); cw.Write(name); cw.Write(ex);
    }
    var cdOffset = (uint)ms.Position;
    w.Write(central.ToArray());
    w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)entries.Length); w.Write((ushort)entries.Length);
    w.Write((uint)central.Length); w.Write(cdOffset); w.Write((ushort)0);
    var path = Path.Combine(this._dir, file);
    File.WriteAllBytes(path, ms.ToArray());
    return path;
  }

  /// <summary>An Info-ZIP Unicode Path field (0x7075) for a stored name.</summary>
  private static byte[] UnicodePath(byte[] storedName, string name) {
    var utf8 = Encoding.UTF8.GetBytes(name);
    var field = new List<byte> { 0x75, 0x70 };
    field.AddRange(BitConverter.GetBytes((ushort)(5 + utf8.Length)));
    field.Add(1);
    field.AddRange(BitConverter.GetBytes(Compression.Core.Checksums.Crc32.Compute(storedName)));
    field.AddRange(utf8);
    return [.. field];
  }

  private static List<string> Names(string zip)
    => [.. ArchiveOperations.List(zip, null).Select(e => e.Name).Order(StringComparer.Ordinal)];

  // "Ürlaub Straße.jpg" and "Fotos/Grüße.png" as Windows Explorer writes them (code page 437).
  private static readonly byte[] ExplorerUrlaub = [0x9A, .. "rlaub Stra"u8, 0xE1, .. "e.jpg"u8];
  private static readonly byte[] ExplorerGruesse = [.. "Fotos/Gr"u8, 0x81, 0xE1, .. "e.png"u8];

  [Test, Category("HappyPath")]
  public void GivenNamesInTheDosCodePage_WhenListedAndExtracted_ThenTheyReadAsWritten() {
    var zip = this.Zip("explorer.zip", (ExplorerUrlaub, null, "photo"), (ExplorerGruesse, null, "greeting"));

    Assert.That(Names(zip), Is.EquivalentTo(new[] { "Fotos/Grüße.png", "Ürlaub Straße.jpg" }));
    var outDir = Path.Combine(this._dir, "x");
    ArchiveOperations.Extract(zip, outDir, null, null);
    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(outDir, "Ürlaub Straße.jpg")), Is.EqualTo("photo"));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "Fotos", "Grüße.png")), Is.EqualTo("greeting"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenUtf8WithoutTheFlag_WhenListed_ThenItStaysUtf8() {
    var zip = this.Zip("mac.zip", (Encoding.UTF8.GetBytes("Ürlaub/Café.jpg"), null, "x"), ("plain.txt"u8.ToArray(), null, "y"));

    Assert.That(Names(zip), Is.EquivalentTo(new[] { "Ürlaub/Café.jpg", "plain.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAMatchingUnicodePathField_WhenListed_ThenItWins_AndAStaleOneIsIgnored() {
    byte[] stored = [.. "Gr"u8, 0x81, .. "n.txt"u8]; // code page 437 "Grün.txt"
    var zip = this.Zip("unicode.zip",
      (stored, UnicodePath(stored, "Grün – 绿.txt"), "a"),
      // A field left over from before a rename no longer matches its name: ignored.
      ([.. "Bl"u8, 0x81, .. "te.txt"u8], UnicodePath("old.txt"u8.ToArray(), "Stale.txt"), "b"));

    Assert.That(Names(zip), Is.EquivalentTo(new[] { "Blüte.txt", "Grün – 绿.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenADosCodePageName_WhenAFileIsAdded_ThenTheOldNameStillReadsTheSame() {
    var zip = this.Zip("edit.zip", ([0x9A, .. "ber.txt"u8], null, "old"));
    var add = Path.Combine(this._dir, "new.txt");
    File.WriteAllText(add, "new");

    ArchiveOperations.Add(zip, [new ArchiveInput(add, "new.txt")]);

    Assert.That(Names(zip), Is.EquivalentTo(new[] { "new.txt", "Über.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenADosCodePageName_WhenRemovedByItsListedName_ThenItGoes() {
    var zip = this.Zip("rm.zip", (ExplorerUrlaub, null, "photo"), ("keep.txt"u8.ToArray(), null, "k"));

    ArchiveOperations.Remove(zip, ["Ürlaub Straße.jpg"]);

    Assert.That(Names(zip), Is.EqualTo(new[] { "keep.txt" }));
  }

  [Test, Category("Boundary")]
  public void GivenEveryHighByte_WhenDecodedAsCodePage437_ThenTheMappingIsTheStandardOne() {
    // The BCL's code page 437 is the oracle here; product code carries its own table.
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var high = Enumerable.Range(0x80, 0x80).Select(b => (byte)b).ToArray();

    Assert.That(ZipNameEncoding.Cp437(high), Is.EqualTo(Encoding.GetEncoding(437).GetString(high)));
  }

  [Test, Category("ArchiveExternalInterop")]
  public void GivenUtf8WithoutTheFlag_WhenTheUnarchiverListsIt_ThenItReadsItAsWeDo() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("lsar"))
      Assert.Ignore("lsar (The Unarchiver) in WSL is needed for this oracle.");
    var zip = this.Zip("mac.zip", (Encoding.UTF8.GetBytes("Ürlaub/Café – Straße.jpg"), null, "x"));

    // lsar prints UTF-8; read it as such (the shared WSL helper decodes in the console code page).
    var psi = new System.Diagnostics.ProcessStartInfo("wsl", ["-e", "sh", "-c", $"lsar {FsInteropToolbox.WinToWsl(zip)}"]) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var stdOutTask = p.StandardOutput.ReadToEndAsync();
    var stdErr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    var stdOut = stdOutTask.Result;
    var exitCode = p.ExitCode;

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.Zero, stdErr);
      Assert.That(stdOut, Does.Contain("Ürlaub/Café – Straße.jpg"));
      Assert.That(Names(zip), Is.EqualTo(new[] { "Ürlaub/Café – Straße.jpg" }));
    });
  }

  [Test, Category("ArchiveExternalInterop")]
  public void GivenDosCodePageAndUnicodePathNames_When7ZipListsThem_ThenItReadsThemAsWeDo() {
    // 7-Zip reads unflagged names in the OEM code page (437 or 850, which agree on these
    // characters) and does not guess UTF-8; the UTF-8 case has its own oracle above.
    FsInteropToolbox.Require7z();
    byte[] stored = [.. "Gr"u8, 0x81, .. "n.txt"u8];
    var zip = this.Zip("seven.zip",
      (ExplorerUrlaub, null, "photo"),
      (ExplorerGruesse, null, "greeting"),
      (stored, UnicodePath(stored, "Grün – 绿.txt"), "a"));

    // -sccUTF-8 makes 7-Zip print UTF-8, so the output is read as UTF-8 too.
    var psi = new System.Diagnostics.ProcessStartInfo(FsInteropToolbox.SevenZipPath!, ["l", "-slt", "-sccUTF-8", zip]) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var stdOutTask = p.StandardOutput.ReadToEndAsync();
    var stdErr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    var stdOut = stdOutTask.Result;
    var exitCode = p.ExitCode;

    var theirs = stdOut.Split('\n').Where(l => l.StartsWith("Path = ", StringComparison.Ordinal))
      .Select(l => l["Path = ".Length..].Trim().Replace('\\', '/')).Where(p => !p.EndsWith(".zip", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(exitCode, Is.Zero, stdErr);
      Assert.That(theirs, Is.EqualTo(Names(zip)));
    });
  }
}
