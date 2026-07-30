#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Nilfs2;

namespace Compression.Tests.Nilfs2;

/// <summary>
/// Locks the WORM → R/W promotion for NILFS2. Validates the spec-canonical
/// "append a new logical segment + bump s_last_cno" mutation semantic with
/// the byte-identical-old-segment invariant intact (continuous-snapshot).
/// </summary>
[TestFixture]
public class Nilfs2InPlaceModifyTests {

  private const int SuperblockOffset = 1024;
  private const int LastCnoOffset = 0x38;

  private static byte[] BuildBaseImage(params (string Name, byte[] Data)[] files) {
    var w = new Nilfs2Writer();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    return w.Build();
  }

  private static ulong ReadLastCno(byte[] image)
    => BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(SuperblockOffset + LastCnoOffset, 8));

  // ─────────────────────────────── Capability ───────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesRwScope() {
    var d = new Nilfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "NILFS2 advertises R/W via continuous-snapshot segment-log append.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  // ─────────────────────────────── Add ───────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_PreservesPriorBytesExceptLastCno() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha-payload"u8.ToArray()));
    var oldLen = baseImg.Length;
    var oldCno = ReadLastCno(baseImg);

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Nilfs2InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("beta.bin", new byte[] { 9, 8, 7 })]);

    var afterImg = ms.ToArray();
    Assert.That(afterImg.Length, Is.GreaterThan(oldLen),
      "Add must grow the image by exactly the appended segment.");

    // Byte-identical preservation of [0, oldLen) except the s_last_cno field.
    var lastCnoStart = SuperblockOffset + LastCnoOffset;
    var lastCnoEnd = lastCnoStart + 8;
    for (var i = 0; i < oldLen; ++i) {
      if (i >= lastCnoStart && i < lastCnoEnd) continue;
      Assert.That(afterImg[i], Is.EqualTo(baseImg[i]),
        $"byte at offset {i} changed (must be byte-identical outside s_last_cno).");
    }

    // s_last_cno bumped by exactly +1.
    Assert.That(ReadLastCno(afterImg), Is.EqualTo(oldCno + 1));
  }

  [Test, Category("HappyPath")]
  public void Add_NewFile_RoundTripsThroughReader() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Nilfs2InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("beta.bin", new byte[] { 9, 8, 7 })]);

    ms.Position = 0;
    var r = new Nilfs2Reader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("alpha.txt"));
    Assert.That(names, Does.Contain("beta.bin"));

    var beta = r.Entries.First(e => e.Name == "beta.bin");
    Assert.That(r.Extract(beta), Is.EqualTo(new byte[] { 9, 8, 7 }));
  }

  [Test, Category("HappyPath")]
  public void Add_MultipleFiles_AllSurfaceAfterMerge() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Nilfs2InPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("two.bin", new byte[] { 2 }),
      ArchiveInputInfo.InMemory("three.bin", new byte[] { 3, 3, 3 }),
    ]);

    ms.Position = 0;
    var r = new Nilfs2Reader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("alpha.txt"));
    Assert.That(names, Does.Contain("two.bin"));
    Assert.That(names, Does.Contain("three.bin"));
  }

  // ─────────────────────────────── Replace ───────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_PreservesOldDataBlocksByteIdentical() {
    var alphaOld = "alpha-original-content"u8.ToArray();
    var baseImg = BuildBaseImage(("alpha.txt", alphaOld));
    var oldLen = baseImg.Length;

    // Find the offset of the alpha payload inside the base image (it lives in
    // the writer-private payload region — straight bytes, no encoding).
    var imgSpan = baseImg.AsSpan();
    var alphaOffset = -1;
    for (var i = 2048; i < baseImg.Length - alphaOld.Length; ++i) {
      if (imgSpan.Slice(i, alphaOld.Length).SequenceEqual(alphaOld)) {
        alphaOffset = i;
        break;
      }
    }
    Assert.That(alphaOffset, Is.GreaterThan(-1), "alpha payload must be locatable in base image.");

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Nilfs2InPlaceModifier.Replace(ms, "alpha.txt", "alpha-replaced!"u8.ToArray());

    var after = ms.ToArray();

    // Old alpha bytes are still present at their original offset — the spec's
    // continuous-snapshot invariant.
    Assert.That(after.AsSpan(alphaOffset, alphaOld.Length).ToArray(), Is.EqualTo(alphaOld),
      "Old payload bytes must stay byte-identical at original offset (snapshot).");
    Assert.That(after.Length, Is.GreaterThan(oldLen));
  }

  [Test, Category("HappyPath")]
  public void Replace_ReaderSurfacesNewContent() {
    var baseImg = BuildBaseImage(("alpha.txt", "original"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Nilfs2InPlaceModifier.Replace(ms, "alpha.txt", "replaced"u8.ToArray());

    ms.Position = 0;
    var r = new Nilfs2Reader(ms);
    var alpha = r.Entries.First(e => e.Name == "alpha.txt");
    Assert.That(r.Extract(alpha), Is.EqualTo("replaced"u8.ToArray()),
      "Highest-cno record wins per NILFS2 segment-replay semantic.");
  }

  [Test, Category("Sad")]
  public void Replace_UnknownName_Throws() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Assert.Throws<FileNotFoundException>(
      () => Nilfs2InPlaceModifier.Replace(ms, "does-not-exist.txt", [1, 2, 3]));
  }

  // ─────────────────────────────── Remove ───────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_PreservesOldBlocksByteIdentical_TombstoneInLog() {
    var alphaOld = "removable-payload"u8.ToArray();
    var baseImg = BuildBaseImage(("alpha.txt", alphaOld), ("beta.bin", "beta"u8.ToArray()));
    var oldLen = baseImg.Length;

    var imgSpan = baseImg.AsSpan();
    var alphaOffset = -1;
    for (var i = 2048; i < baseImg.Length - alphaOld.Length; ++i) {
      if (imgSpan.Slice(i, alphaOld.Length).SequenceEqual(alphaOld)) {
        alphaOffset = i;
        break;
      }
    }
    Assert.That(alphaOffset, Is.GreaterThan(-1));

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Nilfs2InPlaceModifier.Remove(ms, ["alpha.txt"]);

    var after = ms.ToArray();
    Assert.That(after.AsSpan(alphaOffset, alphaOld.Length).ToArray(), Is.EqualTo(alphaOld),
      "Removed payload must stay byte-identical at original offset (snapshot recovery).");
    Assert.That(after.Length, Is.GreaterThan(oldLen), "Tombstone segment is appended past EOF.");
  }

  [Test, Category("HappyPath")]
  public void Remove_ReaderDropsEntry() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()), ("beta.bin", "beta"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Nilfs2InPlaceModifier.Remove(ms, ["alpha.txt"]);

    ms.Position = 0;
    var r = new Nilfs2Reader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Not.Contain("alpha.txt"));
    Assert.That(names, Does.Contain("beta.bin"));
  }

  [Test, Category("Boundary")]
  public void Remove_UnknownName_IsNoOp() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    var oldLen = (int)ms.Length;
    var oldCno = ReadLastCno(baseImg);

    Nilfs2InPlaceModifier.Remove(ms, ["does-not-exist"]);

    // No tombstone segment appended, no cno bump — Remove is a no-op when
    // the entry is already absent.
    Assert.That(ms.Length, Is.EqualTo(oldLen));
    Assert.That(ReadLastCno(ms.ToArray()), Is.EqualTo(oldCno));
  }

  // ─────────────────────────────── Snapshot recovery ───────────────────────────────

  [Test, Category("HappyPath")]
  public void Snapshot_OldStateRemainsByteRecoverable() {
    // The load-bearing NILFS2 invariant: every prior segment is a recoverable
    // snapshot because its bytes stay byte-identical at original offsets.
    var initial = "snapshot-marker"u8.ToArray();
    var baseImg = BuildBaseImage(("doc.txt", initial));

    var imgSpan = baseImg.AsSpan();
    var initialOffset = -1;
    for (var i = 2048; i < baseImg.Length - initial.Length; ++i) {
      if (imgSpan.Slice(i, initial.Length).SequenceEqual(initial)) {
        initialOffset = i;
        break;
      }
    }
    Assert.That(initialOffset, Is.GreaterThan(-1));

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Nilfs2InPlaceModifier.Replace(ms, "doc.txt", "newer-version"u8.ToArray());
    ms.Position = 0;
    Nilfs2InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("extra.dat", new byte[] { 0xAA })]);
    ms.Position = 0;
    Nilfs2InPlaceModifier.Remove(ms, ["doc.txt"]);

    var after = ms.ToArray();
    Assert.That(after.AsSpan(initialOffset, initial.Length).ToArray(), Is.EqualTo(initial),
      "Even after Replace+Add+Remove, the original payload bytes remain recoverable at original offsets.");
  }

  // ─────────────────────────────── Mutate-then-extract ───────────────────────────────

  [Test, Category("HappyPath")]
  public void MutateThenExtract_ThroughDescriptor() {
    var d = new Nilfs2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("alpha.txt", "alpha"u8.ToArray()),
    ], new FormatCreateOptions());

    // Add via descriptor.
    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("beta.bin", new byte[] { 1, 2, 3, 4 })]);

    // Replace alpha via the modifier directly (descriptor doesn't expose Replace).
    ms.Position = 0;
    Nilfs2InPlaceModifier.Replace(ms, "alpha.txt", "alpha2"u8.ToArray());

    // Extract through descriptor.
    var outDir = Path.Combine(Path.GetTempPath(), $"nilfs2-rw-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "alpha.txt")), Is.EqualTo("alpha2"u8.ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "beta.bin")), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void DescriptorRemove_DropsEntry() {
    var d = new Nilfs2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("alpha.txt", "alpha"u8.ToArray()),
      ArchiveInputInfo.InMemory("beta.bin", "beta"u8.ToArray()),
    ], new FormatCreateOptions());

    ms.Position = 0;
    d.Remove(ms, ["alpha.txt"]);

    ms.Position = 0;
    var names = d.List(ms, null).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Not.Contain("alpha.txt"));
    Assert.That(names, Does.Contain("beta.bin"));
  }

  // ─────────────────────────────── Sad-path stream contracts ───────────────────────────────

  [Test, Category("Sad")]
  public void Add_RejectsReadOnlyStream() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var readonlyStream = new MemoryStream(baseImg, writable: false);
    Assert.Throws<ArgumentException>(
      () => Nilfs2InPlaceModifier.Add(readonlyStream, [ArchiveInputInfo.InMemory("x", new byte[1])]));
  }

  [Test, Category("Boundary")]
  public void Add_EmptyInputList_IsNoOp() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    var oldLen = (int)ms.Length;
    var oldCno = ReadLastCno(baseImg);

    Nilfs2InPlaceModifier.Add(ms, []);

    Assert.That(ms.Length, Is.EqualTo(oldLen));
    Assert.That(ReadLastCno(ms.ToArray()), Is.EqualTo(oldCno));
  }

  // ─────────────────────────────── Multi-segment chain ───────────────────────────────

  [Test, Category("HappyPath")]
  public void MultipleAppends_EachBumpsCnoByOne() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    var startCno = ReadLastCno(baseImg);

    Nilfs2InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("b.txt", "b"u8.ToArray())]);
    Nilfs2InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("c.txt", "c"u8.ToArray())]);
    Nilfs2InPlaceModifier.Replace(ms, "alpha.txt", "alphaX"u8.ToArray());

    Assert.That(ReadLastCno(ms.ToArray()), Is.EqualTo(startCno + 3));

    ms.Position = 0;
    var r = new Nilfs2Reader(ms);
    Assert.That(r.LastCheckpoint, Is.EqualTo(startCno + 3),
      "Reader surfaces the bumped last-cno from the superblock.");
    var alpha = r.Entries.First(e => e.Name == "alpha.txt");
    Assert.That(r.Extract(alpha), Is.EqualTo("alphaX"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void AddWithExistingName_ActsAsReplace() {
    // IArchiveModifiable.Add contract: "Appends or replaces" — adding an
    // entry whose name already exists must surface the new content.
    var baseImg = BuildBaseImage(("alpha.txt", "v1"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Nilfs2InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("alpha.txt", "v2"u8.ToArray())]);

    ms.Position = 0;
    var r = new Nilfs2Reader(ms);
    var alpha = r.Entries.First(e => e.Name == "alpha.txt");
    Assert.That(r.Extract(alpha), Is.EqualTo("v2"u8.ToArray()));
  }
}
