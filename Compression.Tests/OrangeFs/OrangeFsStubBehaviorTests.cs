#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.OrangeFs;

namespace Compression.Tests.OrangeFs;

/// <summary>
/// Pins the stub-tier surface for <see cref="OrangeFsFormatDescriptor"/>.
/// OrangeFS / PVFS2 DBPF storage objects are opaque server-side bstreams —
/// without the cluster's <c>fs.conf</c> the payload cannot be semantically
/// resolved. These tests prevent silent capability creep (CanCreate/CanModify)
/// and stop the opaque-blob entry shape from drifting.
/// </summary>
[TestFixture]
public class OrangeFsStubBehaviorTests {

  private static byte[] BuildMagicOnly(int payloadLen = 128) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("PVFS").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4, 4), 1u);                 // version
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8, 4), 2u);                 // datastream type
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), (uint)payloadLen);  // object size
    for (var i = 0; i < payloadLen; i++) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new OrangeFsFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "OrangeFS DBPF is stub-tier (opaque storage object) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "OrangeFS DBPF is stub-tier (opaque storage object) — must not advertise CanModify.");

    var image = BuildMagicOnly(payloadLen: 128);
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "FULL.pvfs", "metadata.ini", "object.bin" }),
      "OrangeFS minimal-image surface must be exactly the documented opaque triple.");

    var outDir = Path.Combine(Path.GetTempPath(), "OrangeFsStub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);

      var fullPath = Path.Combine(outDir, "FULL.pvfs");
      Assert.That(File.Exists(fullPath), Is.True, "Extract must produce FULL.pvfs.");
      Assert.That(File.ReadAllBytes(fullPath), Is.EqualTo(image),
        "FULL.pvfs must round-trip the storage object bytes exactly.");

      var objPath = Path.Combine(outDir, "object.bin");
      Assert.That(File.Exists(objPath), Is.True, "Extract must produce object.bin.");
      var expectedPayload = image.AsSpan(16, 128).ToArray();
      Assert.That(File.ReadAllBytes(objPath), Is.EqualTo(expectedPayload),
        "object.bin must round-trip the opaque payload exactly.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new OrangeFsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"OrangeFS Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
