#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Smoke tests confirming every R/W filesystem implements all four <see cref="DefragMode"/>
/// values via the <see cref="DefragRebuilder"/> dispatch. The point is the wiring; per-FS
/// correctness of file content is covered by the per-FS test suites.
/// </summary>
[TestFixture]
public class MultiFsDefragmentTests {

  [TestCase("Iso")]
  [TestCase("Fat")]
  [TestCase("ExFat")]
  [TestCase("Ext")]
  [TestCase("Ntfs")]
  [TestCase("Btrfs")]
  [TestCase("Xfs")]
  public void DescriptorImplementsIArchiveDefragmentable(string formatId) {
    var desc = FormatRegistry.GetById(formatId);
    Assert.That(desc, Is.Not.Null, $"{formatId} descriptor not registered");
    Assert.That(desc, Is.InstanceOf<IArchiveDefragmentable>(),
      $"{formatId} should implement IArchiveDefragmentable");
  }

  // ── ISO 9660 ─────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Iso_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("FIRST.TXT", "alpha"u8.ToArray());
    w.AddFile("SECOND.TXT", "beta"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.Iso.IsoFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Iso.IsoReader(ms);
    var byName = reader.Entries.Where(e => !e.IsDirectory)
                               .ToDictionary(e => e.Name, e => System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName, Contains.Key("FIRST.TXT"));
    Assert.That(byName["FIRST.TXT"], Is.EqualTo("alpha"));
    Assert.That(byName["SECOND.TXT"], Is.EqualTo("beta"));
  }

  [Test]
  public void Iso_CarveHole_ValidatesCapacity() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("BIG.BIN", new byte[100]);
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    // ISO image is small (mostly metadata + 100-byte file), but valid CarveHole
    // sizing is checked by the helper. Ask for a hole bigger than the entire
    // image — must throw.
    Assert.Throws<ArgumentException>(() =>
      new FileSystem.Iso.IsoFormatDescriptor().Defragment(ms,
        new DefragOptions { Mode = DefragMode.CarveHole, HoleSize = ms.Length * 2 }));
  }

  // ── ext2/3/4 ─────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Ext_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("hello.txt", "world"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.Ext.ExtFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Ext.ExtReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("hello.txt"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("world"));
  }

  // ── exFAT ────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void ExFat_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("FOO.TXT", "bar"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.ExFat.ExFatFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.ExFat.ExFatReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("FOO.TXT"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("bar"));
  }

  // ── NTFS ─────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Ntfs_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("note.txt", "hello"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.Ntfs.NtfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Ntfs.NtfsReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("note.txt"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("hello"));
  }

  // ── Btrfs ────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Btrfs_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("data.bin", new byte[] { 1, 2, 3 });
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    new FileSystem.Btrfs.BtrfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Btrfs.BtrfsReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("data.bin"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(reader.Extract(entry!), Is.EqualTo(new byte[] { 1, 2, 3 }));
  }

  // ── XFS ──────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Xfs_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("greet.txt", "hi"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    new FileSystem.Xfs.XfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Xfs.XfsReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("greet.txt"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("hi"));
  }
}
