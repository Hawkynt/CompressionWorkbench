#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.TahoeLafs;

namespace Compression.Tests.TahoeLafs;

/// <summary>
/// Pins the stub-tier surface for <see cref="TahoeLafsFormatDescriptor"/>.
/// Tahoe-LAFS shares are capability-encrypted Reed-Solomon ciphertext blobs
/// — without the read-cap the payload is opaque. These tests prevent silent
/// capability creep (CanCreate/CanModify) and stop the opaque-blob entry shape
/// from drifting.
/// </summary>
[TestFixture]
public class TahoeLafsStubBehaviorTests {

  private static byte[] BuildMagicOnly(int payloadLen = 64) {
    var image = new byte[12 + payloadLen];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 1u);   // immutable share v1
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), (uint)payloadLen);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8, 4), 1u);   // lease count
    for (var i = 0; i < payloadLen; i++) image[12 + i] = (byte)(i ^ 0x5A);
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new TahoeLafsFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "Tahoe-LAFS is stub-tier (encrypted ciphertext) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "Tahoe-LAFS is stub-tier (encrypted ciphertext) — must not advertise CanModify.");

    var image = BuildMagicOnly(payloadLen: 64);
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "FULL.tahoe-share", "metadata.ini", "share.immutable.bin" }),
      "Tahoe-LAFS minimal-share surface must be exactly the documented opaque triple.");

    var outDir = Path.Combine(Path.GetTempPath(), "TahoeLafsStub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);

      var fullPath = Path.Combine(outDir, "FULL.tahoe-share");
      Assert.That(File.Exists(fullPath), Is.True, "Extract must produce FULL.tahoe-share.");
      Assert.That(File.ReadAllBytes(fullPath), Is.EqualTo(image),
        "FULL.tahoe-share must round-trip the share bytes exactly.");

      var sharePath = Path.Combine(outDir, "share.immutable.bin");
      Assert.That(File.Exists(sharePath), Is.True, "Extract must produce share.immutable.bin.");
      var expectedCipher = image.AsSpan(12, 64).ToArray();
      Assert.That(File.ReadAllBytes(sharePath), Is.EqualTo(expectedCipher),
        "share.immutable.bin must round-trip the opaque ciphertext payload exactly.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new TahoeLafsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"Tahoe-LAFS Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
