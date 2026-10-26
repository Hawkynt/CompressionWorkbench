#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.AndroidBootImage;

namespace Compression.Tests.AndroidBoot;

/// <summary>
/// Android boot / vendor_boot images: the layout, header fields and image id the descriptor
/// writes, how it reads its own and hand-built images back, and how edits behave — including
/// the refusals for signed and non-canonical images.
/// </summary>
[TestFixture]
public sealed class AndroidBootImageTests {

  private static readonly AndroidBootImageFormatDescriptor Descriptor = new();

  internal static byte[] Bytes(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  internal static byte[] Create(params (string Name, byte[] Data)[] inputs) {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [.. inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  internal static byte[] Ini(string text) => Encoding.UTF8.GetBytes(text.Replace("\r", ""));

  internal static Dictionary<string, byte[]> Extract(byte[] image) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_abi_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(image)) Descriptor.Extract(ms, dir, null, null);
      return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  internal static byte[] RebuildFromExtraction(byte[] image)
    => Create([.. Extract(image).Select(kv => (kv.Key, kv.Value))]);

  private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

  // ── layout ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenKernelAndRamdisk_WhenCreatedWithDefaults_ThenTheV0HeaderCarriesMkbootimgDefaults() {
    var kernel = Bytes(3000, 1);
    var ramdisk = Bytes(100, 2);
    var image = Create(("kernel", kernel), ("ramdisk", ramdisk));

    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(image, 0, 8), Is.EqualTo("ANDROID!"));
      Assert.That(U32(image, 8), Is.EqualTo(3000));
      Assert.That(U32(image, 12), Is.EqualTo(0x10008000u), "kernel address = base + kernel_offset");
      Assert.That(U32(image, 16), Is.EqualTo(100));
      Assert.That(U32(image, 20), Is.EqualTo(0x11000000u), "ramdisk address = base + ramdisk_offset");
      Assert.That(U32(image, 28), Is.Zero, "no second stage, so its address is zero as mkbootimg writes it");
      Assert.That(U32(image, 32), Is.EqualTo(0x10000100u));
      Assert.That(U32(image, 36), Is.EqualTo(2048));
      Assert.That(U32(image, 40), Is.Zero);
      // header page + 2 kernel pages + 1 ramdisk page
      Assert.That(image.Length, Is.EqualTo(2048 * 4));
      Assert.That(image.AsSpan(2048, 3000).ToArray(), Is.EqualTo(kernel));
      Assert.That(image.AsSpan(2048 * 3, 100).ToArray(), Is.EqualTo(ramdisk));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenComponents_WhenCreated_ThenTheImageIdIsTheMkbootimgSha1() {
    var kernel = Bytes(10, 3);
    var ramdisk = Bytes(20, 4);
    var image = Create(("kernel", kernel), ("ramdisk", ramdisk));

    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    foreach (var part in new[] { kernel, ramdisk, [] }) {
      sha.AppendData(part);
      sha.AppendData(BitConverter.GetBytes((uint)part.Length));
    }
    var expected = new byte[32];
    sha.GetHashAndReset().CopyTo(expected, 0);
    Assert.That(image.AsSpan(576, 32).ToArray(), Is.EqualTo(expected));
  }

  [TestCase(2048), TestCase(4096)]
  [Category("Boundary")]
  public void GivenAKernelOfExactlyOnePage_WhenCreated_ThenNoPaddingPageIsAdded(int page) {
    var image = Create(("kernel", Bytes(page, 5)), ("metadata.ini", Ini($"[android_boot]\npage_size = {page}\n")));
    Assert.That(image.Length, Is.EqualTo(2 * page));
  }

  [Test, Category("Boundary")]
  public void GivenAKernelOfOnePagePlusOneByte_WhenCreated_ThenItTakesTwoPages() {
    var image = Create(("kernel", Bytes(2049, 6)));
    Assert.That(image.Length, Is.EqualTo(3 * 2048));
  }

  [Test, Category("Boundary")]
  public void GivenNoInputs_WhenCreated_ThenTheImageIsOneHeaderPageAndListsOnlyMetadata() {
    var image = Create();
    Assert.That(image.Length, Is.EqualTo(2048));
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
  [Category("RoundTrip")]
  public void GivenEachBootHeaderVersion_WhenExtractedAndRecreated_ThenTheBytesAreIdentical(int version) {
    var inputs = new List<(string, byte[])> {
      ("kernel", Bytes(5000, 10 + version)), ("ramdisk", Bytes(777, 20 + version)),
      ("metadata.ini", Ini($"[android_boot]\nheader_version = {version}\ncmdline = console=ttyMSM0 androidboot.hardware=qcom\nos_version = 12.1.0\nos_patch_level = 2023-07\n" +
        (version < 3 ? "board = cwbboard\n" : ""))),
    };
    if (version < 3) inputs.Add(("second", Bytes(300, 30)));
    if (version is 1 or 2) inputs.Add(("recovery_dtbo", Bytes(1500, 31)));
    if (version == 2) inputs.Add(("dtb", Bytes(2100, 32)));
    if (version == 4) inputs.Add(("boot_signature", Bytes(4096, 33)));
    var image = Create([.. inputs]);

    Assert.That(RebuildFromExtraction(image), Is.EqualTo(image));
  }

  [TestCase(3), TestCase(4)]
  [Category("RoundTrip")]
  public void GivenEachVendorBootVersion_WhenExtractedAndRecreated_ThenTheBytesAreIdentical(int version) {
    var inputs = new List<(string, byte[])> {
      ("dtb", Bytes(3000, 40)),
      ("metadata.ini", Ini($"[android_vendor_boot]\nheader_version = {version}\npage_size = 4096\ncmdline = androidboot.console=ttyS0\nboard = vendorbrd\n")),
    };
    if (version == 3) inputs.Add(("vendor_ramdisk", Bytes(9000, 41)));
    else {
      inputs.Add(("vendor_ramdisk00", Bytes(1000, 42)));
      inputs.Add(("vendor_ramdisk01", Bytes(2001, 43)));
      inputs.Add(("bootconfig", Encoding.ASCII.GetBytes("androidboot.hardware=cwb\n")));
    }
    var image = Create([.. inputs]);

    Assert.That(Encoding.ASCII.GetString(image, 0, 8), Is.EqualTo("VNDRBOOT"));
    Assert.That(RebuildFromExtraction(image), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenAVendorBootV4_WhenListed_ThenFragmentsAndTheirTableFieldsSurface() {
    var image = Create(("vendor_ramdisk00", Bytes(10, 1)), ("vendor_ramdisk01", Bytes(20, 2)),
      ("metadata.ini", Ini("[android_vendor_boot]\nheader_version = 4\n[vendor_ramdisk01]\ntype = 3\nname = dlkm\nboard_id = 0x1,0x2\n")));
    var files = Extract(image);
    var ini = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.Multiple(() => {
      Assert.That(files["vendor_ramdisk00"], Is.EqualTo(Bytes(10, 1)));
      Assert.That(files["vendor_ramdisk01"], Is.EqualTo(Bytes(20, 2)));
      Assert.That(ini, Does.Contain("[vendor_ramdisk01]\ntype = 3\nname = dlkm\nboard_id = 0x00000001,0x00000002,"));
      Assert.That(ini, Does.Contain("[vendor_ramdisk00]\ntype = 1\nname = \n"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAV2CommandLineLongerThan511Bytes_WhenCreated_ThenTheRestSpillsIntoExtraCmdline() {
    var cmdline = new string('a', 511) + "TAIL";
    var image = Create(("metadata.ini", Ini($"[android_boot]\ncmdline = {cmdline}\n")));
    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(image, 64, 511), Is.EqualTo(new string('a', 511)));
      Assert.That(image[64 + 511], Is.Zero);
      Assert.That(Encoding.ASCII.GetString(image, 608, 4), Is.EqualTo("TAIL"));
    });
  }

  [Test]
  public void GivenOneFileUnderAnArbitraryName_WhenCreated_ThenItIsTheKernel() {
    var payload = Bytes(3000, 11);
    var image = Create(("PROBE.BIN", payload));
    var files = Extract(image);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "metadata.ini", "kernel" }));
    Assert.That(files["kernel"], Is.EqualTo(payload));
    Assert.That(image, Is.EqualTo(Create(("kernel", payload))), "the same image mkbootimg --kernel would make");
  }

  [Test]
  public void GivenOneArbitraryFileBesideARamdisk_WhenCreated_ThenItIsTheKernelAndTheRamdiskStays() {
    var files = Extract(Create(("vmlinuz", [1, 2, 3]), ("ramdisk", [4, 5])));
    Assert.That(files["kernel"], Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.That(files["ramdisk"], Is.EqualTo(new byte[] { 4, 5 }));
  }

  // ── refusals on create ───────────────────────────────────────────────

  [Test, Category("Exception")]
  public void GivenTwoArbitraryFileNames_WhenCreated_ThenTheyAreRefused()
    => Assert.That(() => Create(("a.bin", [1]), ("b.bin", [2])), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenAKernelAndAnArbitraryFileName_WhenCreated_ThenTheArbitraryOneIsRefused()
    => Assert.That(() => Create(("kernel", [1]), ("readme.txt", [2])), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenOnlyTheWholeImageView_WhenCreated_ThenItIsRefusedRatherThanTakenAsTheKernel()
    => Assert.That(() => Create(("FULL.img", [1])), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenASecondStageForAV3Image_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(("second", [1]), ("metadata.ini", Ini("[android_boot]\nheader_version = 3\n"))), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenAnOversizedBoardName_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(("metadata.ini", Ini("[android_boot]\nboard = 0123456789abcdef\n"))), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenHeaderVersion5_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(("metadata.ini", Ini("[android_boot]\nheader_version = 5\n"))), Throws.ArgumentException);

  // ── malformed input ──────────────────────────────────────────────────

  [Test, Category("Exception")]
  public void GivenATruncatedHeader_WhenListed_ThenItFallsBackToTheWholeImageWithoutThrowing() {
    var image = Create(("kernel", Bytes(100, 1)))[..600];
    using var ms = new MemoryStream(image);
    var entries = Descriptor.List(ms, null);
    var files = Extract(image);
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "FULL.img" }));
      Assert.That(Encoding.UTF8.GetString(files["metadata.ini"]), Does.Contain("parse_status = partial"));
      Assert.That(files["FULL.img"], Is.EqualTo(image));
    });
  }

  [Test, Category("Exception")]
  public void GivenAKernelSizePastTheEnd_WhenListed_ThenItFallsBackRatherThanReturningShortData() {
    var image = Create(("kernel", Bytes(5000, 1)));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 1_000_000);
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Does.Contain("FULL.img").And.Not.Contain("kernel"));
  }

  [TestCase(0u), TestCase(1000u), TestCase(3000u)]
  [Category("Exception")]
  public void GivenAnInvalidPageSize_WhenParsed_ThenItIsRejected(uint page) {
    var image = Create(("kernel", Bytes(10, 1)));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), page);
    Assert.That(() => AndroidBootImage.Parse(image), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenAnUnknownHeaderVersion_WhenParsed_ThenItIsRejected() {
    var image = Create(("kernel", Bytes(10, 1)));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(40), 9);
    Assert.That(() => AndroidBootImage.Parse(image), Throws.TypeOf<InvalidDataException>());
  }

  // ── edits ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenTheKernelIsReplaced_ThenTheResultEqualsAFreshImageWithThatKernel() {
    var ini = ("metadata.ini", Ini("[android_boot]\nheader_version = 2\ncmdline = quiet\n"));
    var image = Create(("kernel", Bytes(5000, 1)), ("ramdisk", Bytes(4000, 2)), ("dtb", Bytes(100, 3)), ini);
    var newKernel = Bytes(9000, 4);

    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("kernel", newKernel)]);

    var expected = Create(("kernel", newKernel), ("ramdisk", Bytes(4000, 2)), ("dtb", Bytes(100, 3)), ini);
    Assert.That(ms.ToArray(), Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenTheCmdlineIsEditedThroughMetadata_ThenOnlyThatFieldAndNothingElseChanges() {
    var image = Create(("kernel", Bytes(100, 1)), ("metadata.ini", Ini("[android_boot]\ncmdline = old\nboard = keep\n")));
    using var ms = new MemoryStream();
    ms.Write(image);
    var ini = Encoding.UTF8.GetString(Extract(image)["metadata.ini"]).Replace("cmdline = old", "cmdline = new value");
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes(ini))]);

    var edited = ms.ToArray();
    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(edited, 64, 9), Is.EqualTo("new value"));
      Assert.That(Encoding.ASCII.GetString(edited, 48, 4), Is.EqualTo("keep"));
      Assert.That(edited.AsSpan(2048).ToArray(), Is.EqualTo(image.AsSpan(2048).ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenTheRamdiskIsRemoved_ThenItNoLongerListsAndTheKernelIsUntouched() {
    var kernel = Bytes(3000, 1);
    var image = Create(("kernel", kernel), ("ramdisk", Bytes(10, 2)));
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Remove(ms, ["ramdisk"]);
    var files = Extract(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "metadata.ini", "kernel" }));
      Assert.That(files["kernel"], Is.EqualTo(kernel));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAVendorBootV4_WhenAFragmentIsRemoved_ThenTheLaterOnesMoveUp() {
    var image = Create(("vendor_ramdisk00", Bytes(10, 1)), ("vendor_ramdisk01", Bytes(20, 2)), ("vendor_ramdisk02", Bytes(30, 3)),
      ("metadata.ini", Ini("[android_vendor_boot]\nheader_version = 4\n")));
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Remove(ms, ["vendor_ramdisk01"]);
    var files = Extract(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(files["vendor_ramdisk00"], Is.EqualTo(Bytes(10, 1)));
      Assert.That(files["vendor_ramdisk01"], Is.EqualTo(Bytes(30, 3)));
      Assert.That(files.ContainsKey("vendor_ramdisk02"), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenPurged_ThenOnlyTheHeaderRemainsAndItStillParses() {
    var image = Create(("kernel", Bytes(3000, 1)), ("ramdisk", Bytes(10, 2)), ("metadata.ini", Ini("[android_boot]\nheader_version = 1\nboard = brd\n")));
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Purge(ms);
    var purged = ms.ToArray();
    var parsed = AndroidBootImage.Parse(purged);
    Assert.Multiple(() => {
      Assert.That(purged.Length, Is.EqualTo(2048));
      Assert.That(parsed.HeaderVersion, Is.EqualTo(1));
      Assert.That(FileFormat.FirmwareCommon.FirmwareArchive.CString(parsed.Board), Is.EqualTo("brd"));
      Assert.That(parsed.ImageIdValid, Is.True);
    });
  }

  [Test, Category("Exception")]
  public void GivenAV4ImageWithABootSignature_WhenTheKernelIsReplaced_ThenTheEditIsRefusedAndTheImageUntouched() {
    var image = Create(("kernel", Bytes(100, 1)), ("boot_signature", Bytes(4096, 2)), ("metadata.ini", Ini("[android_boot]\nheader_version = 4\n")));
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(() => Descriptor.Add(ms, [ArchiveInputInfo.InMemory("kernel", [9])]), Throws.TypeOf<NotSupportedException>());
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenAV4ImageWithABootSignature_WhenTheSignatureIsRemoved_ThenTheEditIsAllowed() {
    var image = Create(("kernel", Bytes(100, 1)), ("boot_signature", Bytes(4096, 2)), ("metadata.ini", Ini("[android_boot]\nheader_version = 4\n")));
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Remove(ms, ["boot_signature"]);
    Assert.That(Extract(ms.ToArray()).ContainsKey("boot_signature"), Is.False);
  }

  [Test, Category("Exception")]
  public void GivenAnImageWithAnAvbFooter_WhenTheRamdiskIsReplaced_ThenTheEditIsRefused() {
    var footer = new byte[4096];
    "AVBf"u8.CopyTo(footer.AsSpan(footer.Length - 64));
    var image = Create(("kernel", Bytes(100, 1)), ("trailer.bin", footer));
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(() => Descriptor.Add(ms, [ArchiveInputInfo.InMemory("ramdisk", [9])]), Throws.TypeOf<NotSupportedException>());
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("Exception")]
  public void GivenNonZeroPagePadding_WhenEdited_ThenTheEditIsRefusedButTheImageStillReads() {
    var image = Create(("kernel", Bytes(100, 1)), ("ramdisk", Bytes(10, 2)));
    image[2048 + 100] = 0xEE; // inside the kernel's page padding
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Multiple(() => {
      Assert.That(Extract(image)["kernel"], Is.EqualTo(Bytes(100, 1)));
      Assert.That(() => Descriptor.Remove(ms, ["ramdisk"]), Throws.TypeOf<NotSupportedException>());
      Assert.That(ms.ToArray(), Is.EqualTo(image));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAForeignImageIdThatIsNotTheSha_WhenEdited_ThenTheIdIsKeptAsItWas() {
    var image = Create(("kernel", Bytes(100, 1)), ("ramdisk", Bytes(10, 2)));
    image.AsSpan(576, 32).Clear();
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Remove(ms, ["ramdisk"]);
    Assert.That(ms.ToArray().AsSpan(576, 32).ToArray(), Is.All.Zero);
  }

  [Test, Category("HappyPath")]
  public void GivenHeaderBytesPastTheKnownFields_WhenEdited_ThenTheyArePreserved() {
    var image = Create(("kernel", Bytes(100, 1)), ("ramdisk", Bytes(10, 2)));
    image[2000] = 0x5A; // header page, beyond the v0 fields
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("ramdisk", Bytes(20, 3))]);
    Assert.That(ms.ToArray()[2000], Is.EqualTo(0x5A));
  }
}
