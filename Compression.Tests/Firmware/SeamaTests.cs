#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Seama;

namespace Compression.Tests.Firmware;

/// <summary>
/// SEAMA images: the two OpenWrt <c>seama</c> wrote (one entity, and a seal of two — see
/// ReferenceVectors/README.md) are read and rewritten byte for byte, and our edits pass
/// <c>oseama info</c>.
/// </summary>
[TestFixture]
public sealed class SeamaTests {

  private static readonly SeamaFormatDescriptor Descriptor = new();

  private static byte[] VectorInput(int k, int length) => Enumerable.Range(0, length).Select(i => (byte)((i * (31 + 2 * k) + 7 * k + 1) & 0xFF)).ToArray();

  private static byte[] Create(params (string Name, byte[] Data)[] inputs) {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [.. inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> Extract(byte[] image) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_seama_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(image)) Descriptor.Extract(ms, dir, null, null);
      return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  private static byte[] Ini(string text) => Encoding.ASCII.GetBytes(text);

  [Test, Category("HappyPath")]
  public void GivenSeamasSingleEntity_WhenExtracted_ThenImageMetaAndMd5Check() {
    var files = Extract(FirmwareVectors.Load("openwrt-seama-entity.seama"));
    var ini = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.Multiple(() => {
      Assert.That(files["image.bin"], Is.EqualTo(VectorInput(1, 5000)));
      Assert.That(ini, Does.Contain("kind = entity").And.Contain("meta_0 = dev=/dev/mtdblock/1").And.Contain("meta_1 = type=firmware"));
      // md5sum of the input, as recorded beside the vector
      Assert.That(ini, Does.Contain("md5_stored = e608e0f186f57bb08498b3ba3eb5b271").And.Contain("md5_ok = true"));
      Assert.That(ini, Does.Contain("canonical = true"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenSeamasSeal_WhenExtracted_ThenBothEntitiesAndTheSignatureSurface() {
    var files = Extract(FirmwareVectors.Load("openwrt-seama-seal.seama"));
    var ini = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.Multiple(() => {
      Assert.That(files["entity_00.bin"], Is.EqualTo(VectorInput(1, 5000)));
      Assert.That(files["entity_01.bin"], Is.EqualTo(VectorInput(2, 77)));
      Assert.That(ini, Does.Contain("kind = seal").And.Contain("meta_0 = signature=wrgac01_dlink.2013gui_dir850"));
      Assert.That(ini, Does.Contain("[entity_01]\nmeta_0 = dev=/dev/mtdblock/2\nmeta_1 = type=rootfs"));
    });
  }

  [Test, Category("RoundTrip")]
  public void GivenTheInputsSeamaPacked_WhenWeCreateTheEntity_ThenTheBytesAreIdentical()
    => Assert.That(Create(("image.bin", VectorInput(1, 5000)), ("metadata.ini", Ini("[seama]\nmeta_0 = dev=/dev/mtdblock/1\nmeta_1 = type=firmware\n"))),
      Is.EqualTo(FirmwareVectors.Load("openwrt-seama-entity.seama")));

  [Test, Category("RoundTrip")]
  public void GivenTheInputsSeamaSealed_WhenWeCreateTheSeal_ThenTheBytesAreIdentical()
    => Assert.That(Create(("entity_00.bin", VectorInput(1, 5000)), ("entity_01.bin", VectorInput(2, 77)), ("metadata.ini", Ini(
        "[seama]\nmeta_0 = signature=wrgac01_dlink.2013gui_dir850\n[entity_00]\nmeta_0 = dev=/dev/mtdblock/1\nmeta_1 = type=firmware\n" +
        "[entity_01]\nmeta_0 = dev=/dev/mtdblock/2\nmeta_1 = type=rootfs\n"))),
      Is.EqualTo(FirmwareVectors.Load("openwrt-seama-seal.seama")));

  [TestCase("openwrt-seama-entity.seama"), TestCase("openwrt-seama-seal.seama")]
  [Category("RoundTrip")]
  public void GivenAVector_WhenExtractedAndRecreated_ThenTheBytesAreIdentical(string vector) {
    var image = FirmwareVectors.Load(vector);
    Assert.That(Create([.. Extract(image).Select(kv => (kv.Key, kv.Value))]), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenTheSeal_WhenTheRootfsIsReplaced_ThenTheKernelEntityIsUntouchedAndBothMd5sCheck() {
    var image = FirmwareVectors.Load("openwrt-seama-seal.seama");
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("entity_01.bin", VectorInput(3, 3000))]);
    var edited = SeamaImage.Parse(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(edited.Entities[0].Image, Is.EqualTo(VectorInput(1, 5000)));
      Assert.That(edited.Entities[1].Image, Is.EqualTo(VectorInput(3, 3000)));
      Assert.That(edited.Entities.All(e => e.Md5Valid), Is.True);
      Assert.That(ms.ToArray().AsSpan(0, 52 + 5064).ToArray(), Is.EqualTo(image.AsSpan(0, 52 + 5064).ToArray()));
    });
  }

  [Test, Category("Exception")]
  public void GivenAWrongMd5_WhenEdited_ThenTheEditIsRefusedAndTheImageUntouched() {
    var image = FirmwareVectors.Load("openwrt-seama-entity.seama");
    image[^1] ^= 0x55;
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Multiple(() => {
      Assert.That(Encoding.UTF8.GetString(Extract(image)["metadata.ini"]), Does.Contain("md5_ok = false"));
      Assert.That(() => Descriptor.Add(ms, [ArchiveInputInfo.InMemory("image.bin", [1])]), Throws.TypeOf<NotSupportedException>());
      Assert.That(ms.ToArray(), Is.EqualTo(image));
    });
  }

  [Test, Category("Exception")]
  public void GivenAnImageSizePastTheEnd_WhenListed_ThenItFallsBackWithoutThrowing() {
    var image = FirmwareVectors.Load("openwrt-seama-entity.seama");
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8), 1_000_000);
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "FULL.seama" }));
  }

  [Test, Category("Boundary")]
  public void GivenALittleEndianImage_WhenRewritten_ThenItStaysLittleEndian() {
    var image = Create(("image.bin", [1, 2, 3]), ("metadata.ini", Ini("[seama]\nendianness = little\nmeta_0 = a=b\n")));
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image), Is.EqualTo(SeamaImage.Magic));
      Assert.That(Create([.. Extract(image).Select(kv => (kv.Key, kv.Value))]), Is.EqualTo(image));
    });
  }

  [Test, Category("Exception")]
  public void GivenBothAnImageAndEntities_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(("image.bin", [1]), ("entity_00.bin", [2])), Throws.ArgumentException);

  [Test]
  public void GivenOneFileUnderAnArbitraryName_WhenCreated_ThenItIsTheImage() {
    var payload = VectorInput(1, 5000);
    var image = Create(("PROBE.BIN", payload));
    Assert.That(Extract(image)["image.bin"], Is.EqualTo(payload));
    Assert.That(image, Is.EqualTo(Create(("image.bin", payload))));
  }

  [Test, Category("Exception")]
  public void GivenTwoArbitraryFileNames_WhenCreated_ThenTheyAreRefused()
    => Assert.That(() => Create(("a.bin", [1]), ("b.bin", [2])), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenAnImageAndAnArbitraryFileName_WhenCreated_ThenTheArbitraryOneIsRefused()
    => Assert.That(() => Create(("image.bin", [1]), ("readme.txt", [2])), Throws.ArgumentException);

  [Test, Category("ArchiveExternal")]
  public void GivenAnEditedSeal_WhenOseamaReadsIt_ThenItListsTheNewEntity() {
    var oseama = FirmwareUtilsOracle.Require("oseama");
    using var ms = new MemoryStream();
    ms.Write(FirmwareVectors.Load("openwrt-seama-seal.seama"));
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("entity_02.bin", VectorInput(4, 999)), ArchiveInputInfo.InMemory("metadata.ini",
      Ini("[entity_02]\nmeta_0 = dev=/dev/mtdblock/3\nmeta_1 = type=extra\n"))]);
    var (code, output) = FirmwareUtilsOracle.RunOn(oseama, ms.ToArray(), "info", "{0}");
    Assert.That(code, Is.Zero, output);
    Assert.That(output, Does.Contain("Image size:\t999").And.Contain("Meta entry:\ttype=extra"));
  }
}
