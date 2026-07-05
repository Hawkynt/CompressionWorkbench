using Compression.Registry;

namespace Compression.Tests.Cb7;

[TestFixture]
public class Cb7Tests {

  private static readonly byte[] Page1 = "first comic page bytes"u8.ToArray();
  private static readonly byte[] Page2 = "second comic page bytes"u8.ToArray();
  private static readonly byte[] Page3 = "third comic page bytes, added later"u8.ToArray();

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Cb7.Cb7FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cb7"));
    Assert.That(d.Extensions, Contains.Item(".cb7"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".cb7"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    // 7z signature: 37 7A BC AF 27 1C.
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes[0], Is.EqualTo((byte)'7'));
    Assert.That(d.MagicSignatures[0].Bytes[2], Is.EqualTo(0xBC));
  }

  private static MemoryStream CreateArchive(FileFormat.Cb7.Cb7FormatDescriptor d) {
    var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("page01.png", Page1),
      ArchiveInputInfo.InMemory("page02.png", Page2),
    ], new FormatCreateOptions());
    ms.Position = 0;
    return ms;
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Create_Add_Remove_ListExtract() {
    var d = new FileFormat.Cb7.Cb7FormatDescriptor();
    using var ms = CreateArchive(d);

    // Two entries after create.
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "page01.png", "page02.png" }));

    // Add a third page.
    ms.Position = 0;
    ((IArchiveModifiable)d).Add(ms, [ArchiveInputInfo.InMemory("page03.png", Page3)]);
    ms.Position = 0;
    entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "page01.png", "page02.png", "page03.png" }));

    // Remove the first page.
    ms.Position = 0;
    ((IArchiveModifiable)d).Remove(ms, ["page01.png"]);
    ms.Position = 0;
    entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "page02.png", "page03.png" }));

    // Surviving entries extract byte-identical.
    ms.Position = 0;
    var p2 = ((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "page02.png", null);
    ms.Position = 0;
    var p3 = ((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "page03.png", null);
    Assert.That(p2, Is.EqualTo(Page2));
    Assert.That(p3, Is.EqualTo(Page3));
  }

  [Test, Category("EndToEnd")]
  public void SevenZipOracle_Accepts_Cb7() {
    const string sevenZip = "/usr/bin/7z";
    if (!File.Exists(sevenZip))
      Assert.Ignore("/usr/bin/7z not present; skipping oracle cross-check.");

    var d = new FileFormat.Cb7.Cb7FormatDescriptor();
    using var ms = CreateArchive(d);

    var tmp = Path.Combine(Path.GetTempPath(), "cb7_oracle_" + Guid.NewGuid().ToString("N")[..8] + ".cb7");
    try {
      File.WriteAllBytes(tmp, ms.ToArray());
      var psi = new System.Diagnostics.ProcessStartInfo(sevenZip, $"t \"{tmp}\"") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      };
      using var proc = System.Diagnostics.Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      proc.WaitForExit();
      Assert.That(proc.ExitCode, Is.EqualTo(0), $"7z t rejected the CB7 archive:\n{stdout}");
    } finally {
      if (File.Exists(tmp)) File.Delete(tmp);
    }
  }
}
