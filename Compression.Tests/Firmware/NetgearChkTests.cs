#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.NetgearChk;

namespace Compression.Tests.Firmware;

/// <summary>NETGEAR .chk images, held against the two OpenWrt <c>mkchkimg</c> wrote (see ReferenceVectors/README.md and docs/NETGEAR-CHK-ON-DISK.md).</summary>
[TestFixture]
public sealed class NetgearChkTests {

  private static readonly NetgearChkFormatDescriptor Descriptor = new();

  private static byte[] VectorInput(int k, int length) => Enumerable.Range(0, length).Select(i => (byte)((i * (31 + 2 * k) + 7 * k + 1) & 0xFF)).ToArray();

  private static byte[] Create(params (string Name, byte[] Data)[] inputs) {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [.. inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> Extract(byte[] image) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_chk_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(image)) Descriptor.Extract(ms, dir, null, null);
      return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [TestCase(77, 2, 0xa3fe258du)]
  [TestCase(5000, 1, 0xb9b8ba25u)]
  [Category("HappyPath")]
  public void GivenTheGeneratedInputs_WhenChecksummed_ThenTheValuesMatchWhatMkchkimgReported(int length, int k, uint expected)
    => Assert.That(NetgearChkImage.Checksum(VectorInput(k, length)), Is.EqualTo(expected));

  [Test, Category("Boundary")]
  public void GivenNoBytes_WhenChecksummed_ThenTheChecksumIsZero() => Assert.That(NetgearChkImage.Checksum([]), Is.Zero);

  [Test, Category("HappyPath")]
  public void GivenMkchkimgsKernelAndRootfsImage_WhenExtracted_ThenPartsAndChecksumsCheck() {
    var files = Extract(FirmwareVectors.Load("openwrt-mkchkimg-kernel-rootfs.chk"));
    var ini = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.Multiple(() => {
      Assert.That(files["kernel.bin"], Is.EqualTo(VectorInput(1, 5000)));
      Assert.That(files["rootfs.bin"], Is.EqualTo(VectorInput(2, 77)));
      Assert.That(ini, Does.Contain("board_id = U12H139T00_NETGEAR").And.Contain("region = 2 (North America)"));
      Assert.That(ini, Does.Contain("image_checksum = 0x5B10DFB2").And.Contain("checksums_ok = true").And.Contain("canonical = true"));
    });
  }

  [Test, Category("RoundTrip")]
  public void GivenTheInputsMkchkimgPacked_WhenWeCreateTheImages_ThenTheBytesAreIdentical() {
    Assert.Multiple(() => {
      Assert.That(Create(("kernel.bin", VectorInput(1, 5000)), ("metadata.ini", Encoding.ASCII.GetBytes("[chk]\nboard_id = U12H139T00_NETGEAR\nregion = 1\n"))),
        Is.EqualTo(FirmwareVectors.Load("openwrt-mkchkimg-kernel.chk")));
      Assert.That(Create(("kernel.bin", VectorInput(1, 5000)), ("rootfs.bin", VectorInput(2, 77)),
          ("metadata.ini", Encoding.ASCII.GetBytes("[chk]\nboard_id = U12H139T00_NETGEAR\nregion = 2\n"))),
        Is.EqualTo(FirmwareVectors.Load("openwrt-mkchkimg-kernel-rootfs.chk")));
    });
  }

  [TestCase("openwrt-mkchkimg-kernel.chk"), TestCase("openwrt-mkchkimg-kernel-rootfs.chk")]
  [Category("RoundTrip")]
  public void GivenAVector_WhenExtractedAndRecreated_ThenTheBytesAreIdentical(string vector) {
    var image = FirmwareVectors.Load(vector);
    Assert.That(Create([.. Extract(image).Select(kv => (kv.Key, kv.Value))]), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenTheRootfsIsReplaced_ThenAllFourChecksumsAreRecomputed() {
    using var ms = new MemoryStream();
    ms.Write(FirmwareVectors.Load("openwrt-mkchkimg-kernel-rootfs.chk"));
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("rootfs.bin", VectorInput(5, 4321))]);
    var edited = NetgearChkImage.Parse(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(edited.ChecksumsValid, Is.True);
      Assert.That(edited.Rootfs, Is.EqualTo(VectorInput(5, 4321)));
      Assert.That(edited.Kernel, Is.EqualTo(VectorInput(1, 5000)));
    });
  }

  [Test, Category("Exception")]
  public void GivenAFlippedKernelByte_WhenEdited_ThenTheEditIsRefused() {
    var image = FirmwareVectors.Load("openwrt-mkchkimg-kernel.chk");
    image[100] ^= 1;
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Multiple(() => {
      Assert.That(Encoding.UTF8.GetString(Extract(image)["metadata.ini"]), Does.Contain("checksums_ok = false"));
      Assert.That(() => Descriptor.Remove(ms, ["kernel.bin"]), Throws.TypeOf<NotSupportedException>());
      Assert.That(ms.ToArray(), Is.EqualTo(image));
    });
  }

  [Test, Category("Exception")]
  public void GivenAKernelLengthPastTheEnd_WhenListed_ThenItFallsBackWithoutThrowing() {
    var image = FirmwareVectors.Load("openwrt-mkchkimg-kernel.chk");
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(24), 1_000_000);
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "FULL.chk" }));
  }

  [Test, Category("Exception")]
  public void GivenABoardIdLongerThan64Bytes_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(("kernel.bin", [1]), ("metadata.ini", Encoding.ASCII.GetBytes("[chk]\nboard_id = " + new string('x', 65) + "\n"))), Throws.ArgumentException);

  [Test, Category("Boundary")]
  public void GivenNoMetadata_WhenCreated_ThenMkchkimgsDefaultBoardAndRegionAreUsed() {
    var image = Create(("kernel.bin", VectorInput(2, 77)));
    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(image, 40, 18), Is.EqualTo("U12H072T00_NETGEAR"));
      Assert.That(image[8], Is.EqualTo(1));
      // header checksum mkchkimg wrote for exactly this input with its defaults
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(36)), Is.EqualTo(0x81c60d64u));
    });
  }

  [Test]
  public void GivenOneFileUnderAnArbitraryName_WhenCreated_ThenItIsTheKernel() {
    var payload = VectorInput(2, 77);
    var image = Create(("PROBE.BIN", payload));
    Assert.That(Extract(image)["kernel.bin"], Is.EqualTo(payload));
    Assert.That(image, Is.EqualTo(Create(("kernel.bin", payload))));
  }

  [Test, Category("Exception")]
  public void GivenTwoArbitraryFileNames_WhenCreated_ThenTheyAreRefused()
    => Assert.That(() => Create(("a.bin", [1]), ("b.bin", [2])), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenAKernelAndAnArbitraryFileName_WhenCreated_ThenTheArbitraryOneIsRefused()
    => Assert.That(() => Create(("kernel.bin", [1]), ("readme.txt", [2])), Throws.ArgumentException);
}
