using System.Collections.Generic;
using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Core (no external tool) coverage for the genuine O(bytes-added) in-place RAR5
/// append (<see cref="RarInPlaceAdder"/>). Each test proves three things: the
/// merged archive round-trips through this repo's <see cref="RarReader"/> (old +
/// new entries extract byte-identical); the original block region is still present
/// byte-identical at offset 8 (no re-pack happened); and the file grew by only
/// O(new blocks + ENDARC), not by re-emitting the whole image.
/// </summary>
[TestFixture]
[Category("RoundTrip")]
public class RarInPlaceAddTests {

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

  /// <summary>
  /// The byte offset at which the trailing ENDARC block begins in a RAR5 archive,
  /// i.e. the length of the signature + every block up to (not including) ENDARC.
  /// </summary>
  private static long EndArcOffset(byte[] archive) {
    using var ms = new MemoryStream(archive);
    ms.Position = 8; // RAR5 signature
    while (ms.Position < ms.Length) {
      var blockStart = ms.Position;
      var header = ReadHeaderForTest(ms, out var dataSize, out var type, out var hasData);
      if (type == 5) // ENDARC
        return blockStart;
      if (hasData && dataSize > 0)
        ms.Position += dataSize;
      _ = header;
    }
    return -1;
  }

  // Minimal block-walk mirroring the reader, kept local to the test so the proof
  // does not depend on internals.
  private static bool ReadHeaderForTest(Stream s, out long dataSize, out int type, out bool hasData) {
    dataSize = 0; type = -1; hasData = false;
    var crc = new byte[4];
    if (s.Read(crc, 0, 4) < 4) return false;
    var headerSize = (int)ReadVint(s);
    var body = new byte[headerSize];
    var read = 0;
    while (read < headerSize) {
      var n = s.Read(body, read, headerSize - read);
      if (n == 0) return false;
      read += n;
    }
    var off = 0;
    type = (int)ReadVint(body, ref off);
    var flags = (int)ReadVint(body, ref off);
    if ((flags & 0x0001) != 0) ReadVint(body, ref off); // extra area size
    hasData = (flags & 0x0002) != 0;
    if (hasData) dataSize = (long)ReadVint(body, ref off);
    return true;
  }

  private static ulong ReadVint(Stream s) {
    ulong r = 0; var shift = 0;
    while (true) {
      var b = s.ReadByte();
      if (b < 0) break;
      r |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) break;
      shift += 7;
    }
    return r;
  }

  private static ulong ReadVint(byte[] data, ref int off) {
    ulong r = 0; var shift = 0;
    while (off < data.Length) {
      var b = data[off++];
      r |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) break;
      shift += 7;
    }
    return r;
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

  /// <summary>
  /// Performs the in-place add and asserts the byte-additive contract: the old
  /// region [8 .. old-ENDARC-offset) survives verbatim, and the file grew only by
  /// the new blocks plus a small ENDARC delta.
  /// </summary>
  private static byte[] AddInPlaceAndAssertContract(byte[] original, int method,
      IReadOnlyList<(string Name, byte[] Data)> newFiles) {
    var oldEnd = EndArcOffset(original);
    Assert.That(oldEnd, Is.GreaterThan(8), "test archive must have an ENDARC block");
    var oldPrefix = original[8..(int)oldEnd];

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    RarInPlaceAdder.Add(work, [.. newFiles.Select(f => (f.Name, f.Data, (System.DateTimeOffset?)null))], method);
    var result = work.ToArray();

    // In-place proof: the exact old block region still sits at offset 8.
    Assert.That(result.Length, Is.GreaterThanOrEqualTo(8 + oldPrefix.Length),
      "archive must only grow");
    var newPrefix = result[8..(8 + oldPrefix.Length)];
    Assert.That(newPrefix, Is.EqualTo(oldPrefix),
      "existing blocks must be byte-identical after in-place add (no re-pack)");

    // O(bytes-added): total growth bounded by new payload + a small per-file header
    // budget + one ENDARC block — never a whole-image re-pack.
    var newPayload = newFiles.Sum(f => (long)f.Data.Length);
    var budget = newPayload + 256 + 256L * newFiles.Count;
    var growth = result.Length - original.Length;
    Assert.That(growth, Is.LessThanOrEqualTo(budget),
      $"growth {growth} exceeds O(bytes-added) budget {budget}");

    return result;
  }

  [Test]
  public void AddToSingleFileArchive_Store_InPlace() {
    var original = Build(RarConstants.MethodStore, solid: false, [("a.txt", Enc("first file"))]);
    var added = Enc("brand new appended content");
    var result = AddInPlaceAndAssertContract(original, RarConstants.MethodStore, [("b.txt", added)]);

    var all = ReadAll(result);
    Assert.That(all["a.txt"], Is.EqualTo(Enc("first file")));
    Assert.That(all["b.txt"], Is.EqualTo(added));
  }

  [Test]
  public void AddSeveralToSeveral_Store_InPlace() {
    var seed = new List<(string, byte[])>();
    var rng = new System.Random(11);
    for (var i = 0; i < 5; i++) {
      var d = new byte[300 + i * 40]; rng.NextBytes(d);
      seed.Add(($"seed/f{i:D2}.dat", d));
    }
    var original = Build(RarConstants.MethodStore, solid: false, seed);

    var add = new List<(string, byte[])>();
    for (var i = 0; i < 4; i++) {
      var d = new byte[200 + i * 10]; rng.NextBytes(d);
      add.Add(($"new/g{i:D2}.dat", d));
    }
    var result = AddInPlaceAndAssertContract(original, RarConstants.MethodStore, add);

    var all = ReadAll(result);
    foreach (var (name, data) in seed)
      Assert.That(all[name], Is.EqualTo(data), $"seed file {name} changed");
    foreach (var (name, data) in add)
      Assert.That(all[name], Is.EqualTo(data), $"added file {name} mismatch");
  }

  [Test]
  public void AddToCompressedArchive_InPlace() {
    var d1 = new byte[4000];
    for (var i = 0; i < d1.Length; ++i) d1[i] = (byte)(i % 26 + 'A');
    var original = Build(RarConstants.MethodNormal, solid: false, [("packed.bin", d1)]);

    var d2 = new byte[3000];
    for (var i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 10);
    var result = AddInPlaceAndAssertContract(original, RarConstants.MethodNormal, [("added.bin", d2)]);

    var all = ReadAll(result);
    Assert.That(all["packed.bin"], Is.EqualTo(d1));
    Assert.That(all["added.bin"], Is.EqualTo(d2));
  }

  [Test]
  public void AppendNonSolidAfterSolidRun_InPlace_ExistingStillExtract() {
    // A solid archive: the second file reuses the first's dictionary.
    var d1 = new byte[2000];
    var d2 = new byte[2000];
    for (var i = 0; i < d1.Length; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }
    var original = Build(RarConstants.MethodNormal, solid: true,
      [("solid/a.bin", d1), ("solid/b.bin", d2)]);

    // Append a fresh non-solid file; existing solid files must be untouched.
    var added = Enc("appended non-solid after a solid run");
    var result = AddInPlaceAndAssertContract(original, RarConstants.MethodStore, [("c.txt", added)]);

    var all = ReadAll(result);
    Assert.That(all["solid/a.bin"], Is.EqualTo(d1), "first solid file must still extract");
    Assert.That(all["solid/b.bin"], Is.EqualTo(d2), "second solid file must still extract");
    Assert.That(all["c.txt"], Is.EqualTo(added));
  }

  [Test]
  public void NameCollision_FallsBackWithNotSupported() {
    var original = Build(RarConstants.MethodStore, solid: false, [("dup.txt", Enc("original"))]);
    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    Assert.Throws<System.NotSupportedException>(() =>
      RarInPlaceAdder.Add(work, [("dup.txt", Enc("replacement"), (System.DateTimeOffset?)null)]));
  }

  [Test]
  public void RecoveryRecord_FallsBackWithNotSupported() {
    var ms = new MemoryStream();
    using (var w = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore, recoveryPercent: 10)) {
      w.AddFile("a.txt", Enc("with recovery"));
      w.Finish();
    }
    var original = ms.ToArray();

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    Assert.Throws<System.NotSupportedException>(() =>
      RarInPlaceAdder.Add(work, [("b.txt", Enc("new"), (System.DateTimeOffset?)null)]));
  }

  [Test]
  public void DescriptorAdd_PureAddition_StaysInPlace_AndReadsBack() {
    var original = Build(RarConstants.MethodStore, solid: false,
      [("keep/a.txt", Enc("keep a")), ("keep/b.txt", Enc("keep b"))]);
    var oldEnd = EndArcOffset(original);
    var oldPrefix = original[8..(int)oldEnd];

    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new RarFormatDescriptor();
    desc.Add(stream, [ArchiveInputInfo.InMemory("added/c.txt", Enc("added c"))]);

    var result = stream.ToArray();
    var newPrefix = result[8..(8 + oldPrefix.Length)];
    Assert.That(newPrefix, Is.EqualTo(oldPrefix),
      "descriptor.Add must use the in-place path for pure additions (old bytes unchanged)");

    var all = ReadAll(result);
    Assert.That(all["keep/a.txt"], Is.EqualTo(Enc("keep a")));
    Assert.That(all["keep/b.txt"], Is.EqualTo(Enc("keep b")));
    Assert.That(all["added/c.txt"], Is.EqualTo(Enc("added c")));
  }

  [Test]
  public void DescriptorAdd_NameCollision_FallsBackToRebuild_AndReplaces() {
    var original = Build(RarConstants.MethodStore, solid: false, [("dup.txt", Enc("original"))]);
    using var stream = new MemoryStream();
    stream.Write(original, 0, original.Length);
    stream.Position = 0;

    var desc = new RarFormatDescriptor();
    desc.Add(stream, [ArchiveInputInfo.InMemory("dup.txt", Enc("replacement"))]);

    var all = ReadAll(stream.ToArray());
    Assert.That(all["dup.txt"], Is.EqualTo(Enc("replacement")),
      "name collision must fall back to the rebuild which replaces the entry");
  }

  private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
