using System.Collections.Generic;
using Compression.Core.Checksums;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// Core (no external tool) coverage for the genuine O(bytes-added) in-place 7z
/// append (<see cref="SevenZipInPlaceAdder"/>). Each test proves three things:
/// the merged archive round-trips through this repo's <see cref="SevenZipReader"/>
/// (old + new entries extract byte-identical); the original packed region is still
/// present byte-identical at offset 32 (no re-pack happened); and the file grew by
/// only O(new compressed data + header), not by re-emitting the whole image.
/// </summary>
[TestFixture]
[Category("RoundTrip")]
public class SevenZipInPlaceAddTests {

  private static byte[] Build(SevenZipCodec codec,
      IEnumerable<(string Name, byte[] Data, bool Dir)> entries) {
    var ms = new MemoryStream();
    using (var w = new SevenZipWriter(ms, codec, leaveOpen: true)) {
      foreach (var (name, data, dir) in entries) {
        if (dir) w.AddDirectory(name);
        else w.AddEntry(new SevenZipEntry { Name = name, Size = data.Length }, data);
      }
      w.Finish();
    }
    return ms.ToArray();
  }

  private static byte[] PackedRegion(byte[] archive) {
    using var ms = new MemoryStream(archive);
    var sig = ReadSigHeader(ms);
    var region = new byte[sig.headerStart - SevenZipConstants.SignatureHeaderSize];
    System.Array.Copy(archive, SevenZipConstants.SignatureHeaderSize, region, 0, region.Length);
    return region;
  }

  private static (long headerStart, long headerSize) ReadSigHeader(Stream s) {
    s.Position = 0;
    var hdr = new byte[SevenZipConstants.SignatureHeaderSize];
    s.ReadExactly(hdr);
    var nextOffset = System.BitConverter.ToInt64(hdr, 12);
    var nextSize = System.BitConverter.ToInt64(hdr, 20);
    return (SevenZipConstants.SignatureHeaderSize + nextOffset, nextSize);
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

  /// <summary>
  /// Performs the in-place add and asserts the byte-additive contract: the old
  /// packed region survives verbatim at offset 32 and the file grew only by the
  /// new payload plus a small header delta.
  /// </summary>
  private static byte[] AddInPlaceAndAssertContract(byte[] original,
      IReadOnlyList<(string Name, byte[] Data, bool Dir)> newFiles) {
    var oldPacked = PackedRegion(original);

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    SevenZipInPlaceAdder.Add(work, [.. newFiles.Select(f => (f.Name, f.Data, f.Dir))]);
    var result = work.ToArray();

    // In-place proof: the exact old packed bytes still sit at offset 32.
    var newPacked = PackedRegion(result);
    Assert.That(newPacked.Length, Is.GreaterThanOrEqualTo(oldPacked.Length),
      "packed region must only grow");
    var prefix = new byte[oldPacked.Length];
    System.Array.Copy(newPacked, 0, prefix, 0, oldPacked.Length);
    Assert.That(prefix, Is.EqualTo(oldPacked),
      "existing packed solid blocks must be byte-identical after in-place add (no re-pack)");

    // O(bytes-added): total growth must be far smaller than re-emitting the image.
    var newPayload = newFiles.Where(f => !f.Dir).Sum(f => (long)f.Data.Length);
    var growth = result.Length - original.Length;
    // Header overhead per entry is small; cap growth at the new payload plus a
    // generous fixed metadata budget. This excludes any whole-image re-pack.
    var budget = newPayload + 4096 + 64L * newFiles.Count;
    Assert.That(growth, Is.LessThanOrEqualTo(budget),
      $"growth {growth} exceeds O(bytes-added) budget {budget}");

    return result;
  }

  [Test]
  public void AddToSingleFileArchive_Lzma2_InPlace() {
    var original = Build(SevenZipCodec.Lzma2, [("a.txt", Enc("first file"), false)]);
    var added = Enc("brand new appended content");
    var result = AddInPlaceAndAssertContract(original, [("b.txt", added, false)]);

    var all = ReadAll(result);
    Assert.That(all["a.txt"], Is.EqualTo(Enc("first file")));
    Assert.That(all["b.txt"], Is.EqualTo(added));
  }

  [Test]
  public void AddToSolidBlock_Lzma2_InPlace() {
    var d1 = new byte[3000]; System.Array.Fill(d1, (byte)'A');
    var d2 = new byte[5000]; System.Array.Fill(d2, (byte)'B');
    var original = Build(SevenZipCodec.Lzma2,
      [("solid/one.dat", d1, false), ("solid/two.dat", d2, false)]);

    var n1 = new byte[2000]; new System.Random(7).NextBytes(n1);
    var n2 = Enc("second new file");
    var result = AddInPlaceAndAssertContract(original,
      [("new/x.bin", n1, false), ("new/y.txt", n2, false)]);

    var all = ReadAll(result);
    Assert.That(all["solid/one.dat"], Is.EqualTo(d1));
    Assert.That(all["solid/two.dat"], Is.EqualTo(d2));
    Assert.That(all["new/x.bin"], Is.EqualTo(n1));
    Assert.That(all["new/y.txt"], Is.EqualTo(n2));
  }

  [Test]
  public void AddToCopyCodecArchive_InPlace() {
    var original = Build(SevenZipCodec.Copy,
      [("stored1.bin", Enc("stored content one"), false),
       ("stored2.bin", Enc("stored content two"), false)]);

    var added = Enc("appended to a copy-codec archive");
    var result = AddInPlaceAndAssertContract(original, [("added.bin", added, false)]);

    var all = ReadAll(result);
    Assert.That(all["stored1.bin"], Is.EqualTo(Enc("stored content one")));
    Assert.That(all["stored2.bin"], Is.EqualTo(Enc("stored content two")));
    Assert.That(all["added.bin"], Is.EqualTo(added));
  }

  [Test]
  public void AddWithDirectoriesAndEmptyFiles_InPlace() {
    var original = Build(SevenZipCodec.Lzma2,
      [("dir1", [], true), ("dir1/file.txt", Enc("inside dir1"), false)]);

    var payload = Enc("new file under dir2");
    var result = AddInPlaceAndAssertContract(original,
      [("dir2", [], true), ("dir2/new.txt", payload, false), ("empty.txt", [], false)]);

    var all = ReadAll(result);
    Assert.That(all["dir1/file.txt"], Is.EqualTo(Enc("inside dir1")));
    Assert.That(all["dir2/new.txt"], Is.EqualTo(payload));
    Assert.That(all.ContainsKey("dir2"), Is.True);
    Assert.That(all.ContainsKey("empty.txt"), Is.True);
    Assert.That(all["empty.txt"], Is.Empty);
  }

  [Test]
  public void AddManyFilesToManyFileSolidBlock_InPlace() {
    var rng = new System.Random(99);
    var seed = new List<(string, byte[], bool)>();
    for (var i = 0; i < 12; i++) {
      var d = new byte[500 + i * 37]; rng.NextBytes(d);
      seed.Add(($"block/item{i:D2}.dat", d, false));
    }
    var original = Build(SevenZipCodec.Lzma2, seed);

    var added = new List<(string, byte[], bool)>();
    for (var i = 0; i < 8; i++) {
      var d = new byte[300 + i * 11]; rng.NextBytes(d);
      added.Add(($"extra/add{i:D2}.dat", d, false));
    }
    var result = AddInPlaceAndAssertContract(original, added);

    var all = ReadAll(result);
    foreach (var (name, data, _) in seed)
      Assert.That(all[name], Is.EqualTo(data), $"seed file {name} changed");
    foreach (var (name, data, _) in added)
      Assert.That(all[name], Is.EqualTo(data), $"added file {name} mismatch");
  }

  [Test]
  public void NameCollision_FallsBackWithNotSupported() {
    var original = Build(SevenZipCodec.Lzma2, [("dup.txt", Enc("original"), false)]);
    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    Assert.Throws<System.NotSupportedException>(() =>
      SevenZipInPlaceAdder.Add(work, [("dup.txt", Enc("replacement"), false)]));
  }

  [Test]
  public void DescriptorAdd_PureAddition_StaysInPlace_AndReadsBack() {
    var original = Build(SevenZipCodec.Lzma2,
      [("keep/a.txt", Enc("keep a"), false), ("keep/b.txt", Enc("keep b"), false)]);
    var oldPacked = PackedRegion(original);

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new SevenZipFormatDescriptor();
    desc.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("added/c.txt", Enc("added c"))]);

    var result = stream.ToArray();
    var newPacked = PackedRegion(result);
    var prefix = new byte[oldPacked.Length];
    System.Array.Copy(newPacked, 0, prefix, 0, oldPacked.Length);
    Assert.That(prefix, Is.EqualTo(oldPacked),
      "descriptor.Add must use the in-place path for pure additions (old packed bytes unchanged)");

    var all = ReadAll(result);
    Assert.That(all["keep/a.txt"], Is.EqualTo(Enc("keep a")));
    Assert.That(all["keep/b.txt"], Is.EqualTo(Enc("keep b")));
    Assert.That(all["added/c.txt"], Is.EqualTo(Enc("added c")));
  }

  [Test]
  public void DescriptorAdd_NameCollision_FallsBackToRebuild_AndReplaces() {
    var original = Build(SevenZipCodec.Lzma2, [("dup.txt", Enc("original"), false)]);
    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new SevenZipFormatDescriptor();
    desc.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("dup.txt", Enc("replacement"))]);

    var all = ReadAll(stream.ToArray());
    Assert.That(all["dup.txt"], Is.EqualTo(Enc("replacement")),
      "name collision must fall back to the rebuild which replaces the entry");
  }

  private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
