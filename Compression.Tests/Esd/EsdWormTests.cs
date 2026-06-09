using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Esd;
using FileFormat.Wim;

namespace Compression.Tests.Esd;

/// <summary>
/// WORM contract tests for the ESD writer. Creation delegates to
/// <see cref="WimWriter"/> and patches the header flags field with the
/// community-known ESD marker bit; the produced file remains parseable by the
/// standard <see cref="WimReader"/> and round-trips through this descriptor's
/// listing and extract paths.
/// </summary>
[TestFixture]
public class EsdWormTests {

  private static byte[] CreateArchive(IEnumerable<(string Name, byte[] Data)> entries) {
    var d = new EsdFormatDescriptor();
    var inputs = entries.Select(e => ArchiveInputInfo.InMemory(e.Name, e.Data)).ToList();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Capabilities_IncludeCanCreate() {
    var d = new EsdFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo(FormatCapabilities.CanCreate));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Create_StartsWithWimMagic() {
    var bytes = CreateArchive([("first.bin", "alpha"u8.ToArray())]);
    Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(208));
    Assert.That(bytes.AsSpan(0, 8).SequenceEqual("MSWIM\0\0\0"u8), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_SetsEsdMarkerBitInHeaderFlags() {
    var bytes = CreateArchive([("first.bin", "alpha"u8.ToArray())]);

    var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
    Assert.That(flags & 0x00100000u, Is.EqualTo(0x00100000u),
      "ESD marker bit (0x00100000) must be set in the header flags field.");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsThroughEsdListAndExtract() {
    var first = "ESD plaintext one"u8.ToArray();
    var second = "ESD plaintext two"u8.ToArray();
    var bytes = CreateArchive([("a.bin", first), ("b.bin", second)]);

    var d = new EsdFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, null);
    var names = list.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names.Count(n => n.StartsWith("resource_", StringComparison.Ordinal)), Is.EqualTo(2));

    var tmp = Path.Combine(Path.GetTempPath(), "cwb_esd_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms2 = new MemoryStream(bytes);
      d.Extract(ms2, tmp, null, null);
      var resources = Directory.GetFiles(tmp, "resource_*.bin").Select(File.ReadAllBytes).ToList();
      Assert.That(resources.Any(c => c.SequenceEqual(first)), Is.True);
      Assert.That(resources.Any(c => c.SequenceEqual(second)), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Create_ReaderAcceptsMarkerBitWithoutFailure() {
    // The marker bit (0x00100000) is not a compression-type flag and must not
    // disturb the standard WimReader/WimHeader.Read path.
    var bytes = CreateArchive([("payload.bin", Encoding.UTF8.GetBytes("plain payload"))]);

    using var ms = new MemoryStream(bytes);
    var header = WimHeader.Read(ms);
    Assert.That((header.WimFlags & 0x00100000u), Is.EqualTo(0x00100000u));
    Assert.That(header.CompressionType, Is.EqualTo(WimConstants.CompressionNone));
  }
}
