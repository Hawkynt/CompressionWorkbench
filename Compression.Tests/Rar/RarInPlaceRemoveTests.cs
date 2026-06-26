using System.Collections.Generic;
using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Core (no external tool) coverage for the genuine O(bytes-shifted) in-place RAR5
/// removal (<see cref="RarInPlaceRemover"/>). Each in-place test proves: the
/// compacted archive round-trips through this repo's <see cref="RarReader"/>
/// (exactly the survivors list and extract byte-identical); the bytes of any block
/// that precedes the removed hole are byte-identical pre/post (no re-pack); and the
/// archive shrank by ≈ the removed block, not a full rebuild. Removing a file that
/// is part of a solid run must throw <see cref="System.NotSupportedException"/>.
/// </summary>
[TestFixture]
[Category("RoundTrip")]
public class RarInPlaceRemoveTests {

  private static byte[] Build(int method, bool solid,
      IEnumerable<(string Name, byte[] Data)> entries) {
    var ms = new MemoryStream();
    using (var w = new RarWriter(ms, leaveOpen: true, method: method, solid: solid)) {
      foreach (var (name, data) in entries)
        w.AddFile(name, data);
      w.Finish();
    }
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] archive) {
    using var ms = new MemoryStream(archive);
    using var r = new RarReader(ms);
    var map = new Dictionary<string, byte[]>(System.StringComparer.Ordinal);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      map[e.Name] = e.IsDirectory ? [] : r.Extract(i);
    }
    return map;
  }

  private static byte[] RemoveInPlace(byte[] original, params string[] names) {
    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    RarInPlaceRemover.Remove(work, names);
    return work.ToArray();
  }

  [Test]
  public void RemoveOnlyFile_LeavesEmptyArchive_InPlace() {
    var original = Build(RarConstants.MethodStore, solid: false, [("solo.txt", Enc("only"))]);
    var result = RemoveInPlace(original, "solo.txt");

    Assert.That(result.Length, Is.LessThan(original.Length), "removing the only file must shrink");
    var all = ReadAll(result);
    Assert.That(all, Is.Empty);
  }

  [Test]
  public void RemoveMiddleFile_NonSolid_InPlace_PrefixByteIdentical() {
    var a = new byte[1000]; new System.Random(1).NextBytes(a);
    var b = new byte[1200]; new System.Random(2).NextBytes(b);
    var c = new byte[900]; new System.Random(3).NextBytes(c);
    var original = Build(RarConstants.MethodStore, solid: false,
      [("a.dat", a), ("b.dat", b), ("c.dat", c)]);

    // The first block (signature + MAIN + a.dat) precedes the hole and must stay
    // byte-identical; c.dat and ENDARC shift down.
    var prefixLen = 8 + 64; // signature + a chunk spanning MAIN + start of a.dat
    var oldPrefix = original[..prefixLen];

    var result = RemoveInPlace(original, "b.dat");

    var newPrefix = result[..prefixLen];
    Assert.That(newPrefix, Is.EqualTo(oldPrefix),
      "blocks before the removed hole must be byte-identical (no re-pack)");

    var all = ReadAll(result);
    Assert.That(all.ContainsKey("b.dat"), Is.False);
    Assert.That(all["a.dat"], Is.EqualTo(a));
    Assert.That(all["c.dat"], Is.EqualTo(c));
    Assert.That(result.Length, Is.LessThan(original.Length));
  }

  [Test]
  public void RemoveOneOfSeveral_NonSolidCompressed_InPlace() {
    var a = new byte[4000];
    for (var i = 0; i < a.Length; ++i) a[i] = (byte)(i % 26 + 'A');
    var b = new byte[3000];
    for (var i = 0; i < b.Length; ++i) b[i] = (byte)(i % 7);
    var original = Build(RarConstants.MethodNormal, solid: false,
      [("packed/a.bin", a), ("packed/b.bin", b)]);

    var result = RemoveInPlace(original, "packed/a.bin");

    var all = ReadAll(result);
    Assert.That(all.ContainsKey("packed/a.bin"), Is.False);
    Assert.That(all["packed/b.bin"], Is.EqualTo(b), "the survivor must still extract");
    Assert.That(result.Length, Is.LessThan(original.Length));
  }

  [Test]
  public void RemoveSolidFile_ThrowsNotSupported() {
    var d1 = new byte[2000];
    var d2 = new byte[2000];
    for (var i = 0; i < d1.Length; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }
    var original = Build(RarConstants.MethodNormal, solid: true,
      [("solid/a.bin", d1), ("solid/b.bin", d2)]);

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    // a.bin is the first solid file (non-solid bit) but b.bin after it IS solid and
    // reuses a.bin's dictionary, so removing a.bin would break the chain.
    Assert.Throws<System.NotSupportedException>(() =>
      RarInPlaceRemover.Remove(work, ["solid/a.bin"]),
      "removing a file a solid block depends on must fall back");

    // b.bin is itself solid → also unsupported in place.
    using var work2 = new MemoryStream();
    work2.Write(original, 0, original.Length);
    work2.Position = 0;
    Assert.Throws<System.NotSupportedException>(() =>
      RarInPlaceRemover.Remove(work2, ["solid/b.bin"]),
      "removing a solid block member must fall back");
  }

  [Test]
  public void RecoveryRecord_FallsBackWithNotSupported() {
    var ms = new MemoryStream();
    using (var w = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore, recoveryPercent: 10)) {
      w.AddFile("a.txt", Enc("with recovery"));
      w.AddFile("b.txt", Enc("second"));
      w.Finish();
    }
    var original = ms.ToArray();

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    Assert.Throws<System.NotSupportedException>(() =>
      RarInPlaceRemover.Remove(work, ["a.txt"]));
  }

  [Test]
  public void RemoveNonexistentName_NoOp() {
    var original = Build(RarConstants.MethodStore, solid: false, [("a.txt", Enc("a"))]);
    var result = RemoveInPlace(original, "missing.txt");
    Assert.That(result, Is.EqualTo(original), "removing a non-existent name must not change bytes");
  }

  [Test]
  public void DescriptorRemove_NonSolid_StaysInPlace_AndReadsBack() {
    var a = new byte[1500]; new System.Random(4).NextBytes(a);
    var b = new byte[1500]; new System.Random(5).NextBytes(b);
    var original = Build(RarConstants.MethodStore, solid: false, [("a.dat", a), ("b.dat", b)]);
    var oldPrefix = original[..(8 + 32)];

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new RarFormatDescriptor();
    desc.Remove(stream, ["b.dat"]);

    var result = stream.ToArray();
    var newPrefix = result[..(8 + 32)];
    Assert.That(newPrefix, Is.EqualTo(oldPrefix),
      "descriptor.Remove must use the in-place path (blocks before the hole unchanged)");

    var all = ReadAll(result);
    Assert.That(all.ContainsKey("b.dat"), Is.False);
    Assert.That(all["a.dat"], Is.EqualTo(a));
  }

  [Test]
  public void DescriptorRemove_Solid_FallsBackToRebuild() {
    var d1 = new byte[2000];
    var d2 = new byte[2000];
    for (var i = 0; i < d1.Length; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }
    var original = Build(RarConstants.MethodNormal, solid: true,
      [("solid/a.bin", d1), ("solid/b.bin", d2)]);

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new RarFormatDescriptor();
    desc.Remove(stream, ["solid/a.bin"]);

    var all = ReadAll(stream.ToArray());
    Assert.That(all.ContainsKey("solid/a.bin"), Is.False,
      "the rebuild fallback must still remove the entry");
    Assert.That(all["solid/b.bin"], Is.EqualTo(d2), "the survivor must extract intact");
  }

  [Test]
  public void DescriptorUpdate_SameName_NonSolid_InPlace_ReplacesContent() {
    var a = new byte[1500]; new System.Random(6).NextBytes(a);
    var bOld = Enc("old b content");
    var original = Build(RarConstants.MethodStore, solid: false,
      [("a.dat", a), ("b.txt", bOld)]);

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var bNew = Enc("the brand-new b content, a different length entirely");
    var desc = new RarFormatDescriptor();
    desc.Add(stream, [ArchiveInputInfo.InMemory("b.txt", bNew)]);

    var all = ReadAll(stream.ToArray());
    Assert.That(all["a.dat"], Is.EqualTo(a), "untouched entry must be intact");
    Assert.That(all["b.txt"], Is.EqualTo(bNew), "same-name update must replace content");
  }

  private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
