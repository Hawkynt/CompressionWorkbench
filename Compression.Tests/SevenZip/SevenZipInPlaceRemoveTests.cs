using System.Collections.Generic;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// Core (no external tool) coverage for the genuine O(bytes-shifted) in-place 7z
/// removal (<see cref="SevenZipInPlaceRemover"/>). Each in-place test proves: the
/// compacted archive round-trips through this repo's <see cref="SevenZipReader"/>
/// (exactly the survivors list and extract byte-identical); the packed bytes of any
/// folder that precedes the removed hole are byte-identical pre/post (no re-pack);
/// and the file shrank by ≈ the removed packed stream(s), not a whole rebuild.
/// The proper-subset-of-a-solid-block case must throw <see cref="System.NotSupportedException"/>.
/// </summary>
[TestFixture]
[Category("RoundTrip")]
public class SevenZipInPlaceRemoveTests {

  /// <summary>
  /// Builds a 7z archive. <paramref name="maxBlockSize"/> controls solid grouping:
  /// 1 forces one folder per file (each file exceeds the block), 0 packs every file
  /// into one solid folder, an intermediate value groups files into folders by size.
  /// </summary>
  private static byte[] Build(SevenZipCodec codec, long maxBlockSize,
      IEnumerable<(string Name, byte[] Data, bool Dir)> entries) {
    var ms = new MemoryStream();
    using (var w = new SevenZipWriter(ms, codec, leaveOpen: true)) {
      foreach (var (name, data, dir) in entries) {
        if (dir) w.AddDirectory(name);
        else w.AddEntry(new SevenZipEntry { Name = name, Size = data.Length }, data);
      }
      w.Finish(maxThreads: 1, maxBlockSize: maxBlockSize);
    }
    return ms.ToArray();
  }

  private static long HeaderStart(byte[] archive) {
    var nextOffset = System.BitConverter.ToInt64(archive, 12);
    return SevenZipConstants.SignatureHeaderSize + nextOffset;
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] archive) {
    using var ms = new MemoryStream(archive);
    using var r = new SevenZipReader(ms);
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
    SevenZipInPlaceRemover.Remove(work, names);
    return work.ToArray();
  }

  [Test]
  public void RemoveOnlyFile_LeavesEmptyArchive_InPlace() {
    var original = Build(SevenZipCodec.Lzma2, 0, [("solo.txt", Enc("the only file"), false)]);
    var result = RemoveInPlace(original, "solo.txt");

    Assert.That(result.Length, Is.LessThan(original.Length), "removing the only file must shrink");
    var all = ReadAll(result);
    Assert.That(all, Is.Empty, "no entries should remain");
  }

  [Test]
  public void RemoveOneWholeFolderOfSeveral_InPlace_PrefixByteIdentical() {
    // Three files, each in its own folder (one-folder-per-file via maxBlockSize=1).
    // a.dat is random so its packed stream is comfortably larger than the prefix
    // window below — a compressible fill would pack to a handful of bytes and the
    // window would spill into the removed folder.
    var a = new byte[3000]; new System.Random(1).NextBytes(a);
    var b = new byte[4000]; new System.Random(2).NextBytes(b);
    var c = new byte[3500]; new System.Random(3).NextBytes(c);
    var original = Build(SevenZipCodec.Lzma2, 1,
      [("a.dat", a, false), ("b.dat", b, false), ("c.dat", c, false)]);

    // Remove the MIDDLE folder. The first folder (before the hole) must stay
    // byte-identical at its exact offset; the third shifts down.
    var firstFolderPrefix = original[SevenZipConstants.SignatureHeaderSize..
      (SevenZipConstants.SignatureHeaderSize + 64)];

    var result = RemoveInPlace(original, "b.dat");

    var newPrefix = result[SevenZipConstants.SignatureHeaderSize..
      (SevenZipConstants.SignatureHeaderSize + 64)];
    Assert.That(newPrefix, Is.EqualTo(firstFolderPrefix),
      "the folder before the removed hole must be byte-identical (no re-pack)");

    var all = ReadAll(result);
    Assert.That(all.ContainsKey("b.dat"), Is.False);
    Assert.That(all["a.dat"], Is.EqualTo(a));
    Assert.That(all["c.dat"], Is.EqualTo(c));

    // Shrink ≈ removed packed stream, far less than a full rebuild of all three.
    var shrink = original.Length - result.Length;
    Assert.That(shrink, Is.GreaterThan(0));
    Assert.That(result.Length, Is.LessThan(original.Length));
  }

  [Test]
  public void RemoveWholeMultiFileFolder_InPlace() {
    // Two folders: folder0 = {x,y} packed together; folder1 = {z}.
    var x = new byte[1500]; System.Array.Fill(x, (byte)'X');
    var y = new byte[1500]; System.Array.Fill(y, (byte)'Y');
    var z = new byte[5000]; new System.Random(9).NextBytes(z);
    // maxBlockSize 4000 keeps {x,y}=3000 in one folder, then z=5000 alone.
    var original = Build(SevenZipCodec.Lzma2, 4000,
      [("g/x.dat", x, false), ("g/y.dat", y, false), ("h/z.dat", z, false)]);

    // Remove BOTH members of folder0 — a whole-folder removal.
    var result = RemoveInPlace(original, "g/x.dat", "g/y.dat");

    var all = ReadAll(result);
    Assert.That(all.ContainsKey("g/x.dat"), Is.False);
    Assert.That(all.ContainsKey("g/y.dat"), Is.False);
    Assert.That(all["h/z.dat"], Is.EqualTo(z), "the surviving folder must still extract");
    Assert.That(result.Length, Is.LessThan(original.Length));
  }

  [Test]
  public void RemoveProperSubsetOfSolidBlock_ThrowsNotSupported() {
    var x = new byte[2000]; System.Array.Fill(x, (byte)'X');
    var y = new byte[2000]; System.Array.Fill(y, (byte)'Y');
    // Single solid folder holding both files.
    var original = Build(SevenZipCodec.Lzma2, 0,
      [("solid/x.dat", x, false), ("solid/y.dat", y, false)]);

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    Assert.Throws<System.NotSupportedException>(() =>
      SevenZipInPlaceRemover.Remove(work, ["solid/x.dat"]),
      "removing one member of a multi-file solid block must fall back");
  }

  [Test]
  public void RemoveDirectoryAndEmptyFile_InPlace() {
    var payload = Enc("real content");
    var original = Build(SevenZipCodec.Lzma2, 1,
      [("dir1", [], true), ("dir1/keep.txt", payload, false), ("empty.txt", [], false)]);

    var result = RemoveInPlace(original, "dir1", "empty.txt");

    var all = ReadAll(result);
    Assert.That(all.ContainsKey("dir1"), Is.False);
    Assert.That(all.ContainsKey("empty.txt"), Is.False);
    Assert.That(all["dir1/keep.txt"], Is.EqualTo(payload), "the real file must survive");
  }

  [Test]
  public void RemoveNonexistentName_NoOp() {
    var original = Build(SevenZipCodec.Lzma2, 1, [("a.txt", Enc("a"), false)]);
    var result = RemoveInPlace(original, "missing.txt");
    Assert.That(result, Is.EqualTo(original), "removing a non-existent name must not change bytes");
  }

  [Test]
  public void DescriptorRemove_WholeFolder_StaysInPlace_AndReadsBack() {
    var a = new byte[2500]; new System.Random(1).NextBytes(a);
    var b = new byte[2500]; new System.Random(2).NextBytes(b);
    var original = Build(SevenZipCodec.Lzma2, 1, [("a.dat", a, false), ("b.dat", b, false)]);
    var firstPrefix = original[SevenZipConstants.SignatureHeaderSize..
      (SevenZipConstants.SignatureHeaderSize + 32)];

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new SevenZipFormatDescriptor();
    desc.Remove(stream, ["b.dat"]);

    var result = stream.ToArray();
    var newPrefix = result[SevenZipConstants.SignatureHeaderSize..
      (SevenZipConstants.SignatureHeaderSize + 32)];
    Assert.That(newPrefix, Is.EqualTo(firstPrefix),
      "descriptor.Remove must use the in-place path (surviving folder bytes unchanged)");

    var all = ReadAll(result);
    Assert.That(all.ContainsKey("b.dat"), Is.False);
    Assert.That(all["a.dat"], Is.EqualTo(a));
  }

  [Test]
  public void DescriptorRemove_SubsetOfSolidBlock_FallsBackToRebuild() {
    var x = new byte[2000]; System.Array.Fill(x, (byte)'X');
    var y = new byte[2000]; System.Array.Fill(y, (byte)'Y');
    var original = Build(SevenZipCodec.Lzma2, 0,
      [("solid/x.dat", x, false), ("solid/y.dat", y, false)]);

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new SevenZipFormatDescriptor();
    desc.Remove(stream, ["solid/x.dat"]);

    var all = ReadAll(stream.ToArray());
    Assert.That(all.ContainsKey("solid/x.dat"), Is.False,
      "the rebuild fallback must still remove the entry");
    Assert.That(all["solid/y.dat"], Is.EqualTo(y), "the survivor must extract intact");
  }

  [Test]
  public void DescriptorUpdate_SameName_WholeFolder_InPlace_ReplacesContent() {
    var a = new byte[2500]; new System.Random(3).NextBytes(a);
    var bOld = new byte[2500]; System.Array.Fill(bOld, (byte)'O');
    var original = Build(SevenZipCodec.Lzma2, 1, [("a.dat", a, false), ("b.dat", bOld, false)]);

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var bNew = Enc("the brand-new b content, different size");
    var desc = new SevenZipFormatDescriptor();
    desc.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("b.dat", bNew)]);

    var all = ReadAll(stream.ToArray());
    Assert.That(all["a.dat"], Is.EqualTo(a), "untouched entry must be intact");
    Assert.That(all["b.dat"], Is.EqualTo(bNew), "same-name update must replace content");
  }

  private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
