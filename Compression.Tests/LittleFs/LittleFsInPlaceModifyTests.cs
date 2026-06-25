#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.LittleFs;

namespace Compression.Tests.LittleFs;

/// <summary>
/// Locks the WORM → R/W promotion for LittleFS. Validates the metadata-pair
/// ping-pong / copy-on-write in-place semantic: a mutation rewrites only the
/// inactive root-pair half at <c>revision+1</c> and appends new data blocks past
/// the current block count, leaving the active root half and every existing data
/// block byte-identical at its offset.
/// </summary>
[TestFixture]
public class LittleFsInPlaceModifyTests {

  private static byte[] BuildBase(uint blockSize, params (string Name, byte[] Data)[] files) {
    var w = new LittleFsWriter(blockSize);
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    return w.Build();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] img) {
    var r = new LittleFsReader(img);
    return r.Files.ToDictionary(f => f.Path, f => r.ReadFile(f));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesRwScope() {
    var d = new LittleFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Add_RoundTripsAndPreservesActiveRootHalf() {
    var seed = new byte[2000];
    new Random(1).NextBytes(seed);
    var baseImg = BuildBase(4096, ("seed.bin", seed));
    var bs = 4096;

    // The reader prefers the higher-revision half (revA >= revB ? A : B). Both
    // start at rev 1 so A wins — block 0 must stay byte-identical after a mutation.
    var activeHalf = baseImg.AsSpan(0, bs).ToArray();

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    var add = new byte[5000];
    new Random(2).NextBytes(add);
    LittleFsInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("added.bin", add)]);

    var after = ms.ToArray();
    Assert.That(after.Length, Is.GreaterThan(baseImg.Length), "Add appends new blocks.");
    Assert.That(after.AsSpan(0, bs).ToArray(), Is.EqualTo(activeHalf),
      "Active root half (block 0) must stay byte-identical (ping-pong rewrites the inactive half).");

    var files = ReadAll(after);
    Assert.That(files, Does.ContainKey("seed.bin"));
    Assert.That(files, Does.ContainKey("added.bin"));
    Assert.That(files["seed.bin"], Is.EqualTo(seed));
    Assert.That(files["added.bin"], Is.EqualTo(add));
  }

  [Test, Category("HappyPath")]
  public void Add_PreservesExistingCtzDataBlocksByteIdentical() {
    var ctzPayload = new byte[8000]; // forces a CTZ skip-list (> inline cap)
    new Random(3).NextBytes(ctzPayload);
    var baseImg = BuildBase(4096, ("big.bin", ctzPayload));
    var baseLen = baseImg.Length;

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    LittleFsInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("tiny.txt", "hi"u8.ToArray())]);

    var after = ms.ToArray();
    // Every byte of the original image past the root pair (blocks 0,1) stays
    // byte-identical — existing CTZ blocks were not relocated.
    for (var i = 2 * 4096; i < baseLen; ++i)
      Assert.That(after[i], Is.EqualTo(baseImg[i]),
        $"existing data byte at offset {i} changed (CTZ blocks must not move).");

    var files = ReadAll(after);
    Assert.That(files["big.bin"], Is.EqualTo(ctzPayload));
    Assert.That(files["tiny.txt"], Is.EqualTo("hi"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Replace_SurfacesNewContent() {
    var baseImg = BuildBase(4096, ("doc.txt", "v1"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    LittleFsInPlaceModifier.Replace(ms, "doc.txt", "version-two"u8.ToArray());

    var files = ReadAll(ms.ToArray());
    Assert.That(files["doc.txt"], Is.EqualTo("version-two"u8.ToArray()));
  }

  [Test, Category("Sad")]
  public void Replace_UnknownName_Throws() {
    var baseImg = BuildBase(4096, ("doc.txt", "x"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Assert.Throws<FileNotFoundException>(() => LittleFsInPlaceModifier.Replace(ms, "nope.txt", [1]));
  }

  [Test, Category("HappyPath")]
  public void Remove_DropsEntry_KeepsSibling() {
    var baseImg = BuildBase(4096, ("a.txt", "aaa"u8.ToArray()), ("b.txt", "bbb"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    LittleFsInPlaceModifier.Remove(ms, ["a.txt"]);

    var files = ReadAll(ms.ToArray());
    Assert.That(files, Does.Not.ContainKey("a.txt"));
    Assert.That(files, Does.ContainKey("b.txt"));
    Assert.That(files["b.txt"], Is.EqualTo("bbb"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void NestedDir_AddAndRemove_RoundTrips() {
    var baseImg = BuildBase(4096, ("root.txt", "r"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    var nested = new byte[2000];
    new Random(7).NextBytes(nested);
    LittleFsInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("sub/inner.dat", nested)]);

    var files = ReadAll(ms.ToArray());
    Assert.That(files, Does.ContainKey("sub/inner.dat"));
    Assert.That(files["sub/inner.dat"], Is.EqualTo(nested));
    Assert.That(files, Does.ContainKey("root.txt"));

    ms.Position = 0;
    LittleFsInPlaceModifier.Remove(ms, ["sub/inner.dat"]);
    var after = ReadAll(ms.ToArray());
    Assert.That(after, Does.Not.ContainKey("sub/inner.dat"));
    Assert.That(after, Does.ContainKey("root.txt"));
  }

  [Test, Category("HappyPath")]
  public void MultipleMutations_BumpRevisionAndRoundTrip() {
    var baseImg = BuildBase(4096, ("seed.txt", "seed"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    LittleFsInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("one.bin", new byte[] { 1 })]);
    ms.Position = 0;
    LittleFsInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("two.bin", new byte[] { 2, 2 })]);
    ms.Position = 0;
    LittleFsInPlaceModifier.Replace(ms, "seed.txt", "seed2"u8.ToArray());

    var files = ReadAll(ms.ToArray());
    Assert.That(files["seed.txt"], Is.EqualTo("seed2"u8.ToArray()));
    Assert.That(files, Does.ContainKey("one.bin"));
    Assert.That(files, Does.ContainKey("two.bin"));

    // Revision of the winning root half advanced past the original (started at 1).
    var after = ms.ToArray();
    var revA = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(0, 4));
    var revB = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(4096, 4));
    Assert.That(Math.Max(revA, revB), Is.GreaterThan(1u));
  }
}
