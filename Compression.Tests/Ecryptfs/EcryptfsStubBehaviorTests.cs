#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ecryptfs;

namespace Compression.Tests.Ecryptfs;

/// <summary>
/// Pins the stub-tier surface for <see cref="EcryptfsFormatDescriptor"/>.
/// eCryptfs per-file containers wrap AES-CBC ciphertext extents — without the
/// mount passphrase + EFEK packets the payload is opaque. These tests prevent
/// silent capability creep (CanCreate/CanModify) and stop the opaque-blob entry
/// shape from drifting.
/// </summary>
[TestFixture]
public class EcryptfsStubBehaviorTests {

  private static byte[] BuildMagicOnly(int cipherLen = 256) {
    // Reader places ciphertext at offset max(ExtentSize, 4096); we set extent=4096.
    const int extent = 4096;
    var image = new byte[extent + cipherLen];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 0x3C81B7F5u);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(4, 8), 8192ul);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(12, 4), 0u);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16, 4), (uint)extent);
    for (var i = 0; i < cipherLen; i++) image[extent + i] = (byte)((i * 11) ^ 0xC3);
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new EcryptfsFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "eCryptfs is stub-tier (encrypted ciphertext) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "eCryptfs is stub-tier (encrypted ciphertext) — must not advertise CanModify.");

    var image = BuildMagicOnly(cipherLen: 256);
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "FULL.ecryptfs", "metadata.ini", "ciphertext.bin" }),
      "eCryptfs minimal-image surface must be exactly the documented opaque triple.");

    var outDir = Path.Combine(Path.GetTempPath(), "EcryptfsStub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);

      var fullPath = Path.Combine(outDir, "FULL.ecryptfs");
      Assert.That(File.Exists(fullPath), Is.True, "Extract must produce FULL.ecryptfs.");
      Assert.That(File.ReadAllBytes(fullPath), Is.EqualTo(image),
        "FULL.ecryptfs must round-trip the file bytes exactly.");

      var cipherPath = Path.Combine(outDir, "ciphertext.bin");
      Assert.That(File.Exists(cipherPath), Is.True, "Extract must produce ciphertext.bin.");
      var expectedCipher = image.AsSpan(4096).ToArray();
      Assert.That(File.ReadAllBytes(cipherPath), Is.EqualTo(expectedCipher),
        "ciphertext.bin must round-trip the opaque AES-CBC payload exactly.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new EcryptfsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"eCryptfs Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
