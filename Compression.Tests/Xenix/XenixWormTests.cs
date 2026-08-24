using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Xenix;

/// <summary>
/// WORM tests for the Xenix V writer: builds fresh images and verifies the
/// reader can round-trip them. Covers single-file, multi-file, multi-block
/// (cross-zone) bodies, nested directories, empty files, name truncation,
/// duplicate-path rejection and the direct-zone-budget guard.
/// </summary>
[TestFixture]
public class XenixWormTests {

  // ── Round-trip: single file ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Xenix WORM round-trip"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile("notice.txt", content);
      w.Finish();
    }

    ms.Position = 0;
    using var r = new FileSystem.Xenix.XenixReader(ms);
    Assert.That(r.Magic, Is.EqualTo(0x002B5544u));
    Assert.That(r.BlockSize, Is.EqualTo(1024));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("notice.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  // ── Round-trip: multi-file flat layout ──────────────────────────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var a = "alpha"u8.ToArray();
    var b = Encoding.ASCII.GetBytes(new string('B', 500));
    var c = new byte[42];
    for (var i = 0; i < c.Length; i++) c[i] = (byte)i;

    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile("a.txt", a);
      w.AddFile("b.bin", b);
      w.AddFile("c.dat", c);
      w.Finish();
    }

    ms.Position = 0;
    using var r = new FileSystem.Xenix.XenixReader(ms);
    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(byName.Keys, Is.EquivalentTo(new[] { "a.txt", "b.bin", "c.dat" }));
    Assert.That(r.Extract(byName["a.txt"]), Is.EqualTo(a));
    Assert.That(r.Extract(byName["b.bin"]), Is.EqualTo(b));
    Assert.That(r.Extract(byName["c.dat"]), Is.EqualTo(c));
  }

  // ── Round-trip: multi-block file (crosses 1KB zone boundary) ────────────

  [Test, Category("Boundary")]
  public void RoundTrip_FileSpansMultipleZones() {
    // 3 KB body — 3 direct zones at 1 KB each.
    var content = new byte[3 * 1024];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)(i * 31 % 256);

    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile("big.bin", content);
      w.Finish();
    }

    ms.Position = 0;
    using var r = new FileSystem.Xenix.XenixReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  // ── Round-trip: empty file ──────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile("empty.txt", []);
      w.Finish();
    }

    ms.Position = 0;
    using var r = new FileSystem.Xenix.XenixReader(ms);
    var entry = r.Entries.Single();
    Assert.That(entry.Name, Is.EqualTo("empty.txt"));
    Assert.That(entry.Size, Is.EqualTo(0));
    Assert.That(r.Extract(entry), Is.EqualTo(Array.Empty<byte>()));
  }

  // ── Round-trip: nested directories ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_NestedDirectories() {
    var rootFile = "root level"u8.ToArray();
    var nestedFile = "deep file"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile("readme", rootFile);
      w.AddFile("usr/bin/sh", nestedFile);
      w.Finish();
    }

    ms.Position = 0;
    using var r = new FileSystem.Xenix.XenixReader(ms);

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName, Contains.Key("readme"));
    Assert.That(byName, Contains.Key("usr"));
    Assert.That(byName, Contains.Key("usr/bin"));
    Assert.That(byName, Contains.Key("usr/bin/sh"));

    Assert.That(byName["usr"].IsDirectory, Is.True);
    Assert.That(byName["usr/bin"].IsDirectory, Is.True);
    Assert.That(byName["usr/bin/sh"].IsDirectory, Is.False);

    Assert.That(r.Extract(byName["readme"]),       Is.EqualTo(rootFile));
    Assert.That(r.Extract(byName["usr/bin/sh"]),   Is.EqualTo(nestedFile));
  }

  // ── Superblock fields match the reader's expectations ───────────────────

  [Test, Category("HappyPath")]
  public void Superblock_MagicAndTypeCode_AreSet() {
    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile("x", "y"u8.ToArray());
      w.Finish();
    }
    var img = ms.ToArray();

    // Magic at sb+0x3F8 = file offset 1024+1016 = 2040.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(2040, 4)),
      Is.EqualTo(0x002B5544u));
    // Type code at sb+0x3FC = file offset 1024+1020 = 2044; 2 == 1024-byte blocks.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(2044, 4)),
      Is.EqualTo(2u));
  }

  // ── Name truncation: > 14 ASCII bytes is clipped to 14 ──────────────────

  [Test, Category("Boundary")]
  public void LongName_IsTruncatedTo14Bytes() {
    var content = "x"u8.ToArray();
    var longName = "abcdefghijklmnopqrstuvwxyz"; // 26 chars

    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile(longName, content);
      w.Finish();
    }

    ms.Position = 0;
    using var r = new FileSystem.Xenix.XenixReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("abcdefghijklmn")); // first 14
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  // ── Duplicate paths are rejected (Sad path) ─────────────────────────────

  [Test, Category("Sad")]
  public void DuplicatePath_Throws() {
    using var ms = new MemoryStream();
    using var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true);
    w.AddFile("dup", "first"u8.ToArray());
    w.AddFile("dup", "second"u8.ToArray());
    Assert.Throws<InvalidOperationException>(() => w.Finish());
  }

  // ── File-size budget: > 10*1024 bytes throws cleanly (Sad path) ─────────

  [Test, Category("Sad")]
  public void FileBeyondDirectZoneBudget_UsesTheIndirectBlocks() {
    // This used to assert the opposite: that a file past ten blocks was refused.
    // A Xenix inode carries thirteen block numbers — ten direct, then the
    // single-, double- and triple-indirect roots — and the reader beside the
    // writer followed all four, so the volume the writer would not build was one
    // it could read. The kernel's sysv driver mounts what it writes now and
    // hands every byte back.
    var data = new byte[300_000];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i * 19 + 7);

    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      w.AddFile("oversize", data);
      w.Finish();
    }

    ms.Position = 0;
    var reader = new FileSystem.Xenix.XenixReader(ms);
    var entry = reader.Entries.Single(e => !e.IsDirectory);
    Assert.That(reader.Extract(entry), Is.EqualTo(data).AsCollection,
      "a file past the direct blocks came back with different bytes");
  }

  // ── Path-component conflict (file used as parent dir) (Sad path) ────────

  [Test, Category("Sad")]
  public void PathConflictsWithFile_Throws() {
    using var ms = new MemoryStream();
    using var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true);
    w.AddFile("dir", "im a file"u8.ToArray());
    w.AddFile("dir/inside", "x"u8.ToArray());
    Assert.Throws<InvalidOperationException>(() => w.Finish());
  }

  // ── Descriptor.Create path round-trips through reader ───────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("hello.txt", "hi"u8.ToArray()),
      ArchiveInputInfo.InMemory("data.bin",  new byte[256]),
    };

    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    var fileEntries = entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(fileEntries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "hello.txt", "data.bin" }));

    ms.Position = 0;
    var hi = d.ExtractEntryToMemory(ms, "hello.txt", null);
    Assert.That(Encoding.ASCII.GetString(hi), Is.EqualTo("hi"));

    ms.Position = 0;
    var bin = d.ExtractEntryToMemory(ms, "data.bin", null);
    Assert.That(bin, Has.Length.EqualTo(256));
  }

  // ── Descriptor.Create uses leaf-name only when input has path components ─

  [Test, Category("HappyPath")]
  public void Descriptor_Create_FlattenedByFilesOnlyHelper() {
    // Descriptor.Create uses FormatHelpers.FilesOnly which preserves the
    // archive name — so a nested archive name produces a nested layout.
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("etc/passwd", "root::0:0::/:/bin/sh"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    using var r = new FileSystem.Xenix.XenixReader(ms);
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName, Contains.Key("etc"));
    Assert.That(byName, Contains.Key("etc/passwd"));
    Assert.That(byName["etc"].IsDirectory, Is.True);
    Assert.That(byName["etc/passwd"].IsDirectory, Is.False);
  }
}
