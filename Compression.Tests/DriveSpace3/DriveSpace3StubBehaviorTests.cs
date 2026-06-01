#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Pins the stub-tier surface for <see cref="DriveSpace3FormatDescriptor"/>.
/// DriveSpace 3 is a proprietary Microsoft compressed-volume wrapper (DS LZ77 +
/// Huffman) — only the MDBPB header is parsed and the wrapped inner data region
/// is exposed opaque. These tests prevent silent capability creep
/// (CanCreate/CanModify) and stop the opaque-blob entry shape from drifting.
/// </summary>
[TestFixture]
public class DriveSpace3StubBehaviorTests {

  private static byte[] BuildMagicOnly() {
    var image = new byte[4096];
    // JMP placeholder + "MS_DSP3" signature at offset 3.
    image[0] = 0xEB; image[1] = 0x3C; image[2] = 0x90;
    Encoding.ASCII.GetBytes("MS_DSP3").CopyTo(image.AsSpan(3));
    // Leave MDFAT/BitFAT offsets at zero — dataOffset becomes (max(0,0)+1)*512 = 512.
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new DriveSpace3FormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "DriveSpace 3 is stub-tier (proprietary LZ77+Huffman wrapper) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "DriveSpace 3 is stub-tier (proprietary LZ77+Huffman wrapper) — must not advertise CanModify.");

    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "drivespace3-volume.bin" }),
      "DriveSpace 3 minimal-image surface must be exactly the documented opaque inner-volume entry.");

    var outDir = Path.Combine(Path.GetTempPath(), "DriveSpace3Stub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);
      var blobPath = Path.Combine(outDir, "drivespace3-volume.bin");
      Assert.That(File.Exists(blobPath), Is.True, "Extract must produce drivespace3-volume.bin.");
      var roundTrip = File.ReadAllBytes(blobPath);
      // Data region begins at (max(MdfatOffset, BitfatOffset)+1)*512 = 512.
      var expected = image.AsSpan(512).ToArray();
      Assert.That(roundTrip, Is.EqualTo(expected),
        "drivespace3-volume.bin must round-trip the data-region byte range exactly.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new DriveSpace3FormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"DriveSpace 3 Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
