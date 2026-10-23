#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.LinuxKernelImage;

namespace Compression.Tests.Firmware;

/// <summary>
/// Linux kernel boot images: headers laid out per the kernel's boot-protocol documentation are
/// recognised and split, the payload is only reported decompressed when it decodes to something
/// carrying a version banner, and real kernels (TinyCore x86 bzImage, OpenWrt ARM zImage and arm64
/// Image — see <c>CWB_KERNEL_SAMPLES</c>) are read where present.
/// </summary>
[TestFixture]
public sealed class LinuxKernelImageTests {

  private static readonly LinuxKernelImageFormatDescriptor Descriptor = new();

  [OneTimeSetUp]
  public void Init() => Compression.Lib.FormatRegistration.EnsureInitialized();

  private static byte[] Gzip(byte[] data) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    FormatRegistry.GetStreamOps("Gzip")!.Compress(input, output);
    return output.ToArray();
  }

  private static readonly byte[] Kernel = Encoding.ASCII.GetBytes("\x7F" + "ELF....Linux version 6.6.0-cwb (test@cwb) #1 SMP\n" + new string('k', 5000));

  /// <summary>An x86 bzImage carrying only what boot.rst defines: 4 setup sectors, protocol 2.15, a gzip payload.</summary>
  internal static byte[] BzImage(byte[] payload, int payloadOffset = 0x100) {
    const int setupSects = 4;
    var pmStart = (setupSects + 1) * 512;
    var pmSize = (payloadOffset + payload.Length + 15) / 16 * 16;
    var image = new byte[pmStart + pmSize];
    image[0x1F1] = setupSects;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x1F4), (uint)(pmSize / 16));
    image[0x1FE] = 0x55;
    image[0x1FF] = 0xAA;
    "HdrS"u8.CopyTo(image.AsSpan(0x202));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x206), 0x020F);
    const int versionAt = 0x300;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x20E), versionAt - 0x200);
    Encoding.ASCII.GetBytes("6.6.0-cwb (test@cwb) #1 SMP").CopyTo(image, versionAt);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x248), (uint)payloadOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x24C), (uint)payload.Length);
    payload.CopyTo(image, pmStart + payloadOffset);
    return image;
  }

  private static Dictionary<string, byte[]> Extract(byte[] image) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_lki_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(image)) Descriptor.Extract(ms, dir, null, null);
      return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test, Category("HappyPath")]
  public void GivenABzImageWithAGzipPayload_WhenExtracted_ThenSetupPayloadAndVmlinuxComeOut() {
    var payload = Gzip(Kernel);
    var files = Extract(BzImage(payload));
    var ini = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.Multiple(() => {
      Assert.That(files["payload.gz"], Is.EqualTo(payload));
      Assert.That(files["vmlinux"], Is.EqualTo(Kernel));
      Assert.That(files["setup.bin"].Length, Is.EqualTo(5 * 512));
      Assert.That(ini, Does.Contain("kind = x86 bzImage").And.Contain("boot_protocol = 2.15").And.Contain("kernel_version = 6.6.0-cwb (test@cwb) #1 SMP"));
      Assert.That(ini, Does.Contain("linux_banner = Linux version 6.6.0-cwb"));
    });
  }

  [Test, Category("Exception")]
  public void GivenAPayloadThatDoesNotDecode_WhenExtracted_ThenNoVmlinuxIsInvented() {
    var payload = Gzip(Kernel);
    payload[20] ^= 0xFF;
    payload[21] ^= 0xFF;
    var files = Extract(BzImage(payload));
    Assert.Multiple(() => {
      Assert.That(files.ContainsKey("vmlinux"), Is.False);
      Assert.That(files["payload.gz"], Is.EqualTo(payload));
    });
  }

  [Test, Category("Exception")]
  public void GivenAPayloadThatDecodesWithoutABanner_WhenExtracted_ThenItIsNotCalledAKernel() {
    var files = Extract(BzImage(Gzip(new byte[4096])));
    Assert.That(files.ContainsKey("vmlinux"), Is.False);
  }

  [Test, Category("Exception")]
  public void GivenSetupSectorsPastTheEnd_WhenListed_ThenItFallsBackWithoutThrowing() {
    var image = BzImage(Gzip(Kernel))[..0x300];
    image[0x1F1] = 60;
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "FULL.bin" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAnArm64ImageHeader_WhenListed_ThenTheHeaderFieldsAreRendered() {
    var image = new byte[4096];
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(16), 4096);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(24), 0b1010); // LE, 4K pages
    "ARMd"u8.CopyTo(image.AsSpan(0x38));
    var ini = Encoding.UTF8.GetString(Extract(image)["metadata.ini"]);
    Assert.That(ini, Does.Contain("kind = arm64 Image").And.Contain("image_size = 4096").And.Contain("page_size = 4K").And.Contain("endianness = little"));
  }

  [Test, Category("HappyPath")]
  public void GivenAnEfiZbootImage_WhenExtracted_ThenThePayloadDecodesToTheImage() {
    var payload = Gzip(Kernel);
    var image = new byte[0x200 + payload.Length];
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    "zimg"u8.CopyTo(image.AsSpan(4));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)payload.Length);
    "gzip"u8.CopyTo(image.AsSpan(0x18));
    payload.CopyTo(image, 0x200);
    var files = Extract(image);
    Assert.Multiple(() => {
      Assert.That(files["Image"], Is.EqualTo(Kernel));
      Assert.That(Encoding.UTF8.GetString(files["metadata.ini"]), Does.Contain("compression_type = gzip"));
    });
  }

  // ── real kernels ─────────────────────────────────────────────────────

  private static byte[] Sample(string name) {
    var dir = Environment.GetEnvironmentVariable("CWB_KERNEL_SAMPLES");
    var path = dir is null ? null : Path.Combine(dir, name);
    if (path is null || !File.Exists(path)) Assert.Ignore($"real kernel sample {name} not present (set CWB_KERNEL_SAMPLES; see the fixture's remarks).");
    return File.ReadAllBytes(path!);
  }

  /// <summary>TinyCore Linux 15.x x86 <c>vmlinuz</c>, http://tinycorelinux.net/15.x/x86/release/distribution_files/vmlinuz.</summary>
  [Test, Category("ArchiveExternal")]
  public void GivenTinyCoresBzImage_WhenExtracted_ThenTheDecompressedVmlinuxIsAnElfWithItsBanner() {
    var files = Extract(Sample("tc-vmlinuz"));
    var ini = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.That(ini, Does.Contain("kind = x86 bzImage"));
    Assert.That(files.ContainsKey("vmlinux"), Is.True, ini);
    Assert.That(files["vmlinux"].AsSpan(0, 4).ToArray(), Is.EqualTo(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }));
    Assert.That(ini, Does.Contain("linux_banner = Linux version 6."));
  }

  /// <summary>OpenWrt 23.05.5 armsr/armv7 generic kernel (ARM zImage).</summary>
  [Test, Category("ArchiveExternal")]
  public void GivenOpenWrtsArmZImage_WhenExtracted_ThenTheDecompressedImageCarriesTheBanner() {
    var files = Extract(Sample("owrt-armv7-kernel.bin"));
    var ini = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.That(ini, Does.Contain("kind = ARM zImage"));
    Assert.That(files.ContainsKey("Image"), Is.True, ini);
    Assert.That(ini, Does.Contain("linux_banner = Linux version 5.15."));
  }

  /// <summary>OpenWrt 23.05.5 armsr/armv8 generic kernel (arm64 Image).</summary>
  [Test, Category("ArchiveExternal")]
  public void GivenOpenWrtsArm64Image_WhenListed_ThenTheHeaderAndBannerAreRendered() {
    var data = Sample("owrt-armv8-kernel.bin");
    var ini = Encoding.UTF8.GetString(Extract(data)["metadata.ini"]);
    Assert.That(ini, Does.Contain("kind = arm64 Image").And.Contain("linux_banner = Linux version 5.15."));
  }
}
