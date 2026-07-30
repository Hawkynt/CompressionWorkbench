#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Nilfs1;

namespace Compression.Tests.Nilfs1;

/// <summary>
/// Locks the WORM → R/W promotion for NILFS v1. Validates the log-structured
/// "append a new logical segment + bump s_last_cno" mutation with the
/// byte-identical-old-segment invariant intact (continuous-snapshot) — the same
/// semantic NILFS2 uses, adapted to NILFS v1's superblock layout.
/// </summary>
[TestFixture]
public class Nilfs1InPlaceModifyTests {

  private const int SuperblockOffset = 1024;
  private const int LastCnoOffset = 0x38;

  private static byte[] BuildBaseImage(params (string Name, byte[] Data)[] files) {
    var w = new Nilfs1Writer();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    return w.Build();
  }

  private static ulong ReadLastCno(byte[] image)
    => BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(SuperblockOffset + LastCnoOffset, 8));

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesRwScope() {
    var d = new Nilfs1FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Add_PreservesPriorBytesExceptLastCno() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha-payload"u8.ToArray()));
    var oldLen = baseImg.Length;
    var oldCno = ReadLastCno(baseImg);

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;

    Nilfs1InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("beta.bin", new byte[] { 9, 8, 7 })]);

    var afterImg = ms.ToArray();
    Assert.That(afterImg.Length, Is.GreaterThan(oldLen),
      "Add must grow the image by exactly the appended segment.");

    var lastCnoStart = SuperblockOffset + LastCnoOffset;
    var lastCnoEnd = lastCnoStart + 8;
    for (var i = 0; i < oldLen; ++i) {
      if (i >= lastCnoStart && i < lastCnoEnd) continue;
      Assert.That(afterImg[i], Is.EqualTo(baseImg[i]),
        $"byte at offset {i} changed (must be byte-identical outside s_last_cno).");
    }

    Assert.That(ReadLastCno(afterImg), Is.EqualTo(oldCno + 1));
  }

  [Test, Category("HappyPath")]
  public void Add_NewFile_RoundTripsThroughReader() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Nilfs1InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("beta.bin", new byte[] { 9, 8, 7 })]);

    ms.Position = 0;
    using var r = new Nilfs1Reader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("alpha.txt"));
    Assert.That(names, Does.Contain("beta.bin"));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "beta.bin")), Is.EqualTo(new byte[] { 9, 8, 7 }));
  }

  [Test, Category("HappyPath")]
  public void Replace_PreservesOldDataBlocksByteIdentical_AndSurfacesNew() {
    var alphaOld = "alpha-original-content"u8.ToArray();
    var baseImg = BuildBaseImage(("alpha.txt", alphaOld));

    var imgSpan = baseImg.AsSpan();
    var alphaOffset = -1;
    for (var i = 2048; i < baseImg.Length - alphaOld.Length; ++i)
      if (imgSpan.Slice(i, alphaOld.Length).SequenceEqual(alphaOld)) { alphaOffset = i; break; }
    Assert.That(alphaOffset, Is.GreaterThan(-1));

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Nilfs1InPlaceModifier.Replace(ms, "alpha.txt", "alpha-replaced!"u8.ToArray());

    var after = ms.ToArray();
    Assert.That(after.AsSpan(alphaOffset, alphaOld.Length).ToArray(), Is.EqualTo(alphaOld),
      "Old payload bytes must stay byte-identical at original offset (snapshot).");

    ms.Position = 0;
    using var r = new Nilfs1Reader(ms);
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "alpha.txt")), Is.EqualTo("alpha-replaced!"u8.ToArray()));
  }

  [Test, Category("Sad")]
  public void Replace_UnknownName_Throws() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Assert.Throws<FileNotFoundException>(
      () => Nilfs1InPlaceModifier.Replace(ms, "does-not-exist.txt", [1, 2, 3]));
  }

  [Test, Category("HappyPath")]
  public void Remove_PreservesOldBytes_AndDropsEntry() {
    var alphaOld = "removable-payload"u8.ToArray();
    var baseImg = BuildBaseImage(("alpha.txt", alphaOld), ("beta.bin", "beta"u8.ToArray()));

    var imgSpan = baseImg.AsSpan();
    var alphaOffset = -1;
    for (var i = 2048; i < baseImg.Length - alphaOld.Length; ++i)
      if (imgSpan.Slice(i, alphaOld.Length).SequenceEqual(alphaOld)) { alphaOffset = i; break; }
    Assert.That(alphaOffset, Is.GreaterThan(-1));

    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    Nilfs1InPlaceModifier.Remove(ms, ["alpha.txt"]);

    var after = ms.ToArray();
    Assert.That(after.AsSpan(alphaOffset, alphaOld.Length).ToArray(), Is.EqualTo(alphaOld),
      "Removed payload must stay byte-identical at original offset (snapshot recovery).");

    ms.Position = 0;
    using var r = new Nilfs1Reader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Not.Contain("alpha.txt"));
    Assert.That(names, Does.Contain("beta.bin"));
  }

  [Test, Category("HappyPath")]
  public void MutateThenExtract_ThroughDescriptor() {
    var d = new Nilfs1FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("alpha.txt", "alpha"u8.ToArray())], new FormatCreateOptions());

    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("beta.bin", new byte[] { 1, 2, 3, 4 })]);
    ms.Position = 0;
    Nilfs1InPlaceModifier.Replace(ms, "alpha.txt", "alpha2"u8.ToArray());

    var outDir = Path.Combine(Path.GetTempPath(), $"nilfs1-rw-{Guid.NewGuid():N}");
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

  [Test, Category("Boundary")]
  public void Remove_UnknownName_IsNoOp() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    var oldLen = (int)ms.Length;
    var oldCno = ReadLastCno(baseImg);

    Nilfs1InPlaceModifier.Remove(ms, ["does-not-exist"]);

    Assert.That(ms.Length, Is.EqualTo(oldLen));
    Assert.That(ReadLastCno(ms.ToArray()), Is.EqualTo(oldCno));
  }

  [Test, Category("HappyPath")]
  public void MultipleAppends_EachBumpsCnoByOne() {
    var baseImg = BuildBaseImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(baseImg);
    ms.Position = 0;
    var startCno = ReadLastCno(baseImg);

    Nilfs1InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("b.txt", "b"u8.ToArray())]);
    Nilfs1InPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("c.txt", "c"u8.ToArray())]);
    Nilfs1InPlaceModifier.Replace(ms, "alpha.txt", "alphaX"u8.ToArray());

    Assert.That(ReadLastCno(ms.ToArray()), Is.EqualTo(startCno + 3));

    ms.Position = 0;
    using var r = new Nilfs1Reader(ms);
    Assert.That(r.LastCheckpoint, Is.EqualTo(startCno + 3));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "alpha.txt")), Is.EqualTo("alphaX"u8.ToArray()));
  }
}
