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
  [TestCase("Mfs")]
  [TestCase("Cpm")]
  [TestCase("TrDos")]
  [TestCase("Lif")]
  [TestCase("Rt11")]
  [TestCase("Os9Rbf")]
  [TestCase("Ufs")]
  [TestCase("DoubleSpace")]
  [TestCase("DriveSpace")]
  [TestCase("MinixFs")]
  [TestCase("SquashFs")]
  [TestCase("CramFs")]
  [TestCase("RomFs")]
  [TestCase("T64")]
  [TestCase("Tap")]
  [TestCase("Msa")]
  [TestCase("ZxScl")]
  // Phase: rebuild-capable FSes added later (writer + reader both real).
  [TestCase("Apfs")]
  [TestCase("Ext1")]
  [TestCase("F2fs")]
  [TestCase("Jfs")]
  [TestCase("ReiserFs")]
  [TestCase("TFat")]
  [TestCase("Udf")]
  [TestCase("Zfs")]
  // Phase: descriptor advertises IArchiveDefragmentable but the method
  // throws NotSupported because the FS lacks a writer / a content-aware
  // reader. The capability is surfaced so callers can probe honestly.
  [TestCase("BcacheFs")]
  [TestCase("Reiser4")]
  [TestCase("Sfs")]
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

  // ── MFS (Macintosh File System) ──────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Mfs_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Mfs.MfsWriter();
    w.AddFile("Note", "macintosh"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.Mfs.MfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Mfs.MfsReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => e.Name.EndsWith("Note"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("macintosh"));
  }

  // ── CP/M ─────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Cpm_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var image = FileSystem.Cpm.CpmWriter.Build([("HELLO.TXT", "world"u8.ToArray(), (byte)0)]);
    using var ms = new MemoryStream();
    ms.Write(image);

    new FileSystem.Cpm.CpmFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    using var copy = new MemoryStream();
    ms.CopyTo(copy);
    var v = FileSystem.Cpm.CpmReader.Read(copy.GetBuffer().AsSpan(0, (int)copy.Length));
    var f = v.Files.FirstOrDefault(x => x.FullName == "HELLO.TXT");
    Assert.That(f, Is.Not.Null);
    // CP/M stores records in whole 1024-byte allocation blocks, so check the prefix.
    Assert.That(System.Text.Encoding.ASCII.GetString(f!.Data, 0, "world".Length), Is.EqualTo("world"));
  }

  // ── TR-DOS (ZX Spectrum) ─────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void TrDos_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.TrDos.TrDosWriter();
    w.AddFile("HELLO", 'C', "spectrum"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.TrDos.TrDosFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.TrDos.TrDosReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => e.Name.StartsWith("HELLO"));
    Assert.That(entry, Is.Not.Null);
    // TR-DOS stores files padded to whole 256-byte sectors.
    var data = reader.Extract(entry!);
    Assert.That(System.Text.Encoding.ASCII.GetString(data, 0, "spectrum".Length), Is.EqualTo("spectrum"));
  }

  // ── HP LIF ───────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Lif_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var image = FileSystem.Lif.LifWriter.Build([("HPFILE", "hewlett"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(image);

    new FileSystem.Lif.LifFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    using var copy = new MemoryStream();
    ms.CopyTo(copy);
    var v = FileSystem.Lif.LifReader.Read(copy.GetBuffer().AsSpan(0, (int)copy.Length));
    var f = v.Files.FirstOrDefault(x => x.Name.StartsWith("HPFILE"));
    Assert.That(f, Is.Not.Null);
    var data = FileSystem.Lif.LifReader.Extract(v, f!);
    // LIF stores data padded to whole 256-byte sectors, so trim to original length.
    Assert.That(System.Text.Encoding.ASCII.GetString(data, 0, "hewlett".Length), Is.EqualTo("hewlett"));
  }

  // ── DEC RT-11 ────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Rt11_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var image = FileSystem.Rt11.Rt11Writer.Build([("FOO.TXT", "pdpeleven"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(image);

    new FileSystem.Rt11.Rt11FormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    using var copy = new MemoryStream();
    ms.CopyTo(copy);
    var v = FileSystem.Rt11.Rt11Reader.Read(copy.GetBuffer().AsSpan(0, (int)copy.Length));
    var f = v.Files.FirstOrDefault(x => x.Name.StartsWith("FOO.TXT"));
    Assert.That(f, Is.Not.Null);
    var data = FileSystem.Rt11.Rt11Reader.Extract(v, f!);
    // RT-11 stores data padded to whole 512-byte blocks; trim to original length.
    Assert.That(System.Text.Encoding.ASCII.GetString(data, 0, "pdpeleven".Length), Is.EqualTo("pdpeleven"));
  }

  // ── OS-9 RBF ─────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Os9Rbf_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var image = FileSystem.Os9Rbf.Os9RbfWriter.Build([("readme", "microware"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(image);

    new FileSystem.Os9Rbf.Os9RbfFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    using var copy = new MemoryStream();
    ms.CopyTo(copy);
    var v = FileSystem.Os9Rbf.Os9RbfReader.Read(copy.GetBuffer().AsSpan(0, (int)copy.Length));
    var f = v.Files.FirstOrDefault(x => !x.IsDirectory && x.Name == "readme");
    Assert.That(f, Is.Not.Null);
    var data = FileSystem.Os9Rbf.Os9RbfReader.Extract(v, f!);
    Assert.That(System.Text.Encoding.ASCII.GetString(data), Is.EqualTo("microware"));
  }

  // ── UFS1 ─────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Ufs_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Ufs.UfsWriter();
    w.AddFile("notes.txt", "berkeley"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    new FileSystem.Ufs.UfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Ufs.UfsReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("notes.txt"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("berkeley"));
  }

  // ── ext1 ─────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Ext1_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Ext1.Ext1Writer();
    w.AddFile("readme", "remy-card"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    new FileSystem.Ext1.Ext1FormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Ext1.Ext1Reader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("readme"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("remy-card"));
  }

  // ── JFS1 ─────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Jfs_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Jfs.JfsWriter();
    w.AddFile("readme", "ibmjfs"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    new FileSystem.Jfs.JfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Jfs.JfsReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("readme"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("ibmjfs"));
  }

  // ── ReiserFS v3.6 ────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void ReiserFs_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("note.txt", "hans"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    new FileSystem.ReiserFs.ReiserFsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("note.txt"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("hans"));
  }

  // ── UDF ──────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Udf_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.Udf.UdfWriter();
    w.AddFile("readme.txt", "udfdisk"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    new FileSystem.Udf.UdfFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.Udf.UdfReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("readme.txt"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("udfdisk"));
  }

  // ── TFAT ─────────────────────────────────────────────────────────────

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void TFat_AllRebuildModes_PreserveFiles(DefragMode mode) {
    var w = new FileSystem.TFat.TFatWriter();
    w.AddFile("HELLO.TXT", "trans"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.TFat.TFatFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode });

    ms.Position = 0;
    var reader = new FileSystem.TFat.TFatReader(ms);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("HELLO.TXT"));
    Assert.That(entry, Is.Not.Null);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry!)), Is.EqualTo("trans"));
  }

  // ── Read-only / SB-only FSes — defragment surfaces but throws ───────

  [TestCase("BcacheFs")]
  [TestCase("Reiser4")]
  [TestCase("Sfs")]
  public void ReadOnlyFs_Defragment_ThrowsNotSupported(string formatId) {
    var desc = (IArchiveDefragmentable)FormatRegistry.GetById(formatId)!;
    using var ms = new MemoryStream(new byte[1024]);
    Assert.Throws<NotSupportedException>(() =>
      desc.Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtStart }));
  }
}
