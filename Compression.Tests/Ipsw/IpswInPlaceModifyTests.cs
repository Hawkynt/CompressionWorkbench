using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Ipsw;

namespace Compression.Tests.Ipsw;

[TestFixture]
public class IpswInPlaceModifyTests {

  private static MemoryStream BuildIpsw(params (string Name, byte[] Data)[] entries) {
    var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      foreach (var (name, data) in entries) {
        var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var es = e.Open();
        es.Write(data);
      }
    }
    ms.Position = 0;
    return ms;
  }

  private static List<string> ZipEntryNames(byte[] bytes) {
    using var ms = new MemoryStream(bytes);
    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
    return zip.Entries.Select(e => e.FullName).ToList();
  }

  private static byte[] ZipEntryBytes(byte[] bytes, string name) {
    using var ms = new MemoryStream(bytes);
    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
    var e = zip.GetEntry(name) ?? throw new KeyNotFoundException(name);
    using var es = e.Open();
    using var outMs = new MemoryStream();
    es.CopyTo(outMs);
    return outMs.ToArray();
  }

  // ── Descriptor surface ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsBothInterfaces() {
    var d = new IpswFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  // ── Add ───────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_NewEntryAppearsInListing() {
    using var ms = BuildIpsw(
      ("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ("Firmware/iBSS.im4p", new byte[] { 0xA, 0xB }));

    IpswInPlaceModifier.AddEntry(ms, "Firmware/iBEC.im4p", new byte[] { 0xC, 0xD });

    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Contain("BuildManifest.plist"));
    Assert.That(names, Does.Contain("Firmware/iBSS.im4p"));
    Assert.That(names, Does.Contain("Firmware/iBEC.im4p"));
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesExistingEntryByteContent() {
    var manifest = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><plist><dict/></plist>");
    var ibss = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
    using var ms = BuildIpsw(
      ("BuildManifest.plist", manifest),
      ("Firmware/iBSS.im4p", ibss));

    IpswInPlaceModifier.AddEntry(ms, "Restore.plist", "extra"u8.ToArray());

    var after = ms.ToArray();
    Assert.That(ZipEntryBytes(after, "BuildManifest.plist"), Is.EqualTo(manifest));
    Assert.That(ZipEntryBytes(after, "Firmware/iBSS.im4p"), Is.EqualTo(ibss));
    Assert.That(ZipEntryBytes(after, "Restore.plist"), Is.EqualTo("extra"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Add_ViaDescriptor_AppendsEntry() {
    using var ms = BuildIpsw(("BuildManifest.plist", "<plist/>"u8.ToArray()));
    var d = new IpswFormatDescriptor();

    ((IArchiveModifiable)d).Add(ms, [
      ArchiveInputInfo.InMemory("LLB.iphone15,3.RELEASE.im4p", new byte[] { 0x01, 0x02 }),
    ]);

    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Contain("LLB.iphone15,3.RELEASE.im4p"));
  }

  [Test, Category("RoundTrip")]
  public void Add_SkipsSyntheticEntries() {
    using var ms = BuildIpsw(("BuildManifest.plist", "<plist/>"u8.ToArray()));
    var d = new IpswFormatDescriptor();

    ((IArchiveModifiable)d).Add(ms, [
      ArchiveInputInfo.InMemory("FULL.ipsw", new byte[] { 0xFF }),
      ArchiveInputInfo.InMemory("metadata.ini", new byte[] { 0xEE }),
    ]);

    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Not.Contain("FULL.ipsw"));
    Assert.That(names, Does.Not.Contain("metadata.ini"));
    Assert.That(names, Has.Count.EqualTo(1));
  }

  // ── Remove ────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_DropsEntryFromListing() {
    using var ms = BuildIpsw(
      ("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ("Firmware/iBSS.im4p", new byte[] { 0xA }),
      ("058-90000-000.dmg", new byte[] { 0xB }));

    var ok = IpswInPlaceModifier.RemoveEntry(ms, "Firmware/iBSS.im4p");

    Assert.That(ok, Is.True);
    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Not.Contain("Firmware/iBSS.im4p"));
    Assert.That(names, Does.Contain("BuildManifest.plist"));
    Assert.That(names, Does.Contain("058-90000-000.dmg"));
  }

  [Test, Category("RoundTrip")]
  public void Remove_PreservesOtherEntryByteContent() {
    var manifest = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><plist><dict/></plist>");
    var dmg = Enumerable.Repeat((byte)0x55, 64).ToArray();
    using var ms = BuildIpsw(
      ("BuildManifest.plist", manifest),
      ("Firmware/iBSS.im4p", new byte[] { 0xA, 0xB, 0xC }),
      ("058-90000-000.dmg", dmg));

    IpswInPlaceModifier.RemoveEntry(ms, "Firmware/iBSS.im4p");

    var after = ms.ToArray();
    Assert.That(ZipEntryBytes(after, "BuildManifest.plist"), Is.EqualTo(manifest));
    Assert.That(ZipEntryBytes(after, "058-90000-000.dmg"), Is.EqualTo(dmg));
  }

  [Test, Category("RoundTrip")]
  public void Remove_ViaDescriptor_DropsEntry() {
    using var ms = BuildIpsw(
      ("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ("Firmware/iBSS.im4p", new byte[] { 0xA }));
    var d = new IpswFormatDescriptor();

    ((IArchiveModifiable)d).Remove(ms, ["Firmware/iBSS.im4p"]);

    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Not.Contain("Firmware/iBSS.im4p"));
  }

  // ── Create ────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Create_ProducesValidIpsw() {
    var d = new IpswFormatDescriptor();
    using var ms = new MemoryStream();

    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ArchiveInputInfo.InMemory("Firmware/iBSS.im4p", new byte[] { 0xA }),
    ], new FormatCreateOptions());

    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Contain("BuildManifest.plist"));
    Assert.That(names, Does.Contain("Firmware/iBSS.im4p"));
  }

  [Test, Category("RoundTrip")]
  public void Create_DropsSyntheticEntries() {
    var d = new IpswFormatDescriptor();
    using var ms = new MemoryStream();

    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("FULL.ipsw", new byte[] { 0xFF }),
      ArchiveInputInfo.InMemory("metadata.ini", new byte[] { 0xEE }),
      ArchiveInputInfo.InMemory("BuildManifest.plist", "<plist/>"u8.ToArray()),
    ], new FormatCreateOptions());

    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Not.Contain("FULL.ipsw"));
    Assert.That(names, Does.Not.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("BuildManifest.plist"));
  }

  // ── Mutate-then-Extract round-trip ───────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_AddThenList() {
    var d = new IpswFormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("BuildManifest.plist", "<plist/>"u8.ToArray()),
    ], new FormatCreateOptions());

    ((IArchiveModifiable)d).Add(ms, [
      ArchiveInputInfo.InMemory("Firmware/iBSS.iphone15,3.RELEASE.im4p", new byte[] { 0xCA, 0xFE }),
    ]);

    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("BuildManifest.plist"));
    Assert.That(names, Has.Some.EqualTo("Firmware/iBSS.iphone15,3.RELEASE.im4p"));
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_RemoveThenList() {
    var d = new IpswFormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ArchiveInputInfo.InMemory("Firmware/iBSS.im4p", new byte[] { 0xA }),
    ], new FormatCreateOptions());

    ((IArchiveModifiable)d).Remove(ms, ["Firmware/iBSS.im4p"]);

    ms.Position = 0;
    var names = ZipEntryNames(ms.ToArray());
    Assert.That(names, Does.Contain("BuildManifest.plist"));
    Assert.That(names, Does.Not.Contain("Firmware/iBSS.im4p"));
  }
}
