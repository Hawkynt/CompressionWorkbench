#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Udf;

namespace Compression.Tests.Udf;

[TestFixture]
public class UdfModifierTests {

  private const int SectorSize = 2048;

  /// <summary>
  /// Builds a UDF image via <see cref="UdfWriter"/> with the given seed files.
  /// Returns a seekable, writable MemoryStream positioned at zero — the format
  /// the modifier expects.
  /// </summary>
  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] seedFiles) {
    var w = new UdfWriter();
    foreach (var (n, d) in seedFiles) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    var capacity = (int)ms.Length;
    // Re-wrap into an expandable MemoryStream — the modifier may grow the image.
    var growable = new MemoryStream(capacity * 4);
    growable.Write(ms.GetBuffer(), 0, capacity);
    growable.SetLength(capacity);
    growable.Position = 0;
    return growable;
  }

  // ── AddFile ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddFile_ToEmptyImage_RoundTrips() {
    using var img = BuildImage(("seed.txt", "seed"u8.ToArray()));

    UdfModifier.AddFile(img, "added.bin", "ADDED"u8.ToArray());

    img.Position = 0;
    var r = new UdfReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "seed.txt", "added.bin" }));
    var added = r.Entries.First(e => e.Name == "added.bin");
    Assert.That(r.Extract(added), Is.EqualTo("ADDED"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddFile_PreservesExistingPayload() {
    var seed = "the seed contents must not be touched"u8.ToArray();
    using var img = BuildImage(("seed.txt", seed));

    UdfModifier.AddFile(img, "extra.dat", new byte[] { 0x10, 0x20, 0x30 });

    img.Position = 0;
    var r = new UdfReader(img);
    var entry = r.Entries.First(e => e.Name == "seed.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(seed));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddFile_MultipleAdds_AllReadable() {
    using var img = BuildImage(("a.txt", "AAA"u8.ToArray()));
    UdfModifier.AddFile(img, "b.txt", "BBB"u8.ToArray());
    UdfModifier.AddFile(img, "c.txt", "CCC"u8.ToArray());
    UdfModifier.AddFile(img, "d.txt", "DDD"u8.ToArray());

    img.Position = 0;
    var r = new UdfReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "a.txt", "b.txt", "c.txt", "d.txt" }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddFile_LargeData_RoundTrips() {
    using var img = BuildImage(("seed.txt", "x"u8.ToArray()));
    var bigData = new byte[12_000];
    new Random(42).NextBytes(bigData);

    UdfModifier.AddFile(img, "big.bin", bigData);

    img.Position = 0;
    var r = new UdfReader(img);
    var entry = r.Entries.First(e => e.Name == "big.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(bigData));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddFile_WithSameName_ReplacesEntry() {
    using var img = BuildImage(("data.bin", "OLD"u8.ToArray()));

    UdfModifier.AddFile(img, "data.bin", "NEW_VALUE"u8.ToArray());

    img.Position = 0;
    var r = new UdfReader(img);
    var entries = r.Entries.Where(e => !e.IsDirectory && e.Name == "data.bin").ToList();
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(entries[0]), Is.EqualTo("NEW_VALUE"u8.ToArray()));
  }

  // ── RemoveFile ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RemoveFile_ExistingEntry_VanishesFromListing() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));

    var removed = UdfModifier.RemoveFile(img, "a.txt");

    Assert.That(removed, Is.True);
    img.Position = 0;
    var r = new UdfReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "b.txt" }));
  }

  [Test, Category("HappyPath")]
  public void RemoveFile_MissingEntry_ReturnsFalse() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()));
    var removed = UdfModifier.RemoveFile(img, "missing.txt");
    Assert.That(removed, Is.False);
  }

  [Test, Category("Security")]
  public void RemoveFile_WipesDataExtent() {
    var unique = "DEADBEEFCAFEBABE_UDF_DATA_BLOCK_MARKER"u8.ToArray();
    using var img = BuildImage(("secret.dat", unique), ("other.dat", "OTHER"u8.ToArray()));

    UdfModifier.RemoveFile(img, "secret.dat", wipeData: true);

    var bytes = img.ToArray();
    var idx = IndexOf(bytes, unique);
    Assert.That(idx, Is.LessThan(0), "wiped block should not contain the unique marker");
  }

  [Test, Category("HappyPath")]
  public void RemoveFile_PreservesSiblings() {
    using var img = BuildImage(
      ("alpha.txt", "ALPHA"u8.ToArray()),
      ("beta.txt", "BETA"u8.ToArray()),
      ("gamma.txt", "GAMMA"u8.ToArray()));

    UdfModifier.RemoveFile(img, "beta.txt");

    img.Position = 0;
    var r = new UdfReader(img);
    var alpha = r.Entries.First(e => e.Name == "alpha.txt");
    var gamma = r.Entries.First(e => e.Name == "gamma.txt");
    Assert.That(r.Extract(alpha), Is.EqualTo("ALPHA"u8.ToArray()));
    Assert.That(r.Extract(gamma), Is.EqualTo("GAMMA"u8.ToArray()));
  }

  // ── Add+Remove cycles ─────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddRemoveAdd_FinalStateCorrect() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()));

    UdfModifier.AddFile(img, "b.txt", "B"u8.ToArray());
    UdfModifier.RemoveFile(img, "a.txt");
    UdfModifier.AddFile(img, "c.txt", "CCC"u8.ToArray());

    img.Position = 0;
    var r = new UdfReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "b.txt", "c.txt" }));
  }

  // ── Descriptor integration ────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Add_UsesInPlaceModifier() {
    using var img = BuildImage(("seed.txt", "S"u8.ToArray()));
    var lengthBefore = img.Length;
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via descriptor"u8.ToArray());
      var d = new UdfFormatDescriptor();
      ((IArchiveModifiable)d).Add(img,
        [new ArchiveInputInfo(tmp, "via.txt", false)]);

      // The image grew (in-place add appends sectors at tail).
      Assert.That(img.Length, Is.GreaterThan(lengthBefore));

      img.Position = 0;
      var entries = d.List(img, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name),
        Has.Member("via.txt"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Remove_UsesInPlaceModifier() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));
    var d = new UdfFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["a.txt"]);

    img.Position = 0;
    var entries = d.List(img, null);
    Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name),
      Is.EquivalentTo(new[] { "b.txt" }));
  }

  // ── Tag CRC integrity (the touched descriptors must still validate) ──────

  [Test, Category("ThemVsUs")]
  public void AfterAdd_PartitionDescriptorTagStillValid() {
    using var img = BuildImage(("seed.txt", "x"u8.ToArray()));
    UdfModifier.AddFile(img, "extra.bin", new byte[200]);
    var bytes = img.ToArray();

    // PD is at sector 33 in writer layout.
    var off = 33 * SectorSize;
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off)), Is.EqualTo((ushort)5),
      "expected Partition Descriptor at sector 33");

    var crcLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 10));
    var storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 8));
    var computed = Compression.Core.Checksums.Crc16Ccitt.Compute(bytes.AsSpan(off + 16, crcLen));
    Assert.That(storedCrc, Is.EqualTo(computed), "PD CRC must remain valid after Add");
  }

  [Test, Category("ThemVsUs")]
  public void AfterAdd_RootFileEntryTagStillValid() {
    using var img = BuildImage(("seed.txt", "x"u8.ToArray()));
    UdfModifier.AddFile(img, "extra.bin", new byte[100]);
    var bytes = img.ToArray();

    // Root FE is at partition LBN 1 (= sector 258 in writer layout).
    var off = 258 * SectorSize;
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off)), Is.EqualTo((ushort)261),
      "expected File Entry at sector 258");

    var crcLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 10));
    var storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 8));
    var computed = Compression.Core.Checksums.Crc16Ccitt.Compute(bytes.AsSpan(off + 16, crcLen));
    Assert.That(storedCrc, Is.EqualTo(computed), "Root FE CRC must remain valid after Add");
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
