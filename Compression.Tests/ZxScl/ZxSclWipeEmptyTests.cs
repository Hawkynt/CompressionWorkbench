#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.ZxScl;

namespace Compression.Tests.ZxScl;

/// <summary>
/// <see cref="IWipeEmpty"/> behavior for SCL: the archive is densely packed, so
/// the only wipeable bytes are cluster tips — the slack between a code/data
/// entry's true byte length (TR-DOS param2) and its 256-byte sector-padded
/// region. Wiping must zero exactly that slack, keep every live byte intact,
/// and re-seal the trailing 32-bit sum-of-bytes checksum so the image stays
/// self-consistent.
/// </summary>
[TestFixture]
public class ZxSclWipeEmptyTests {

  private const int DirectoryStart = 9;
  private const int HeaderSize = ZxSclReader.HeaderSize;   // 14
  private const int SectorSize = ZxSclReader.SectorSize;   // 256

  [Test]
  public void DescriptorOffersWipeEmptyCapability() {
    Assert.That(new ZxSclFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipingClusterTip_ZerosSectorSlack_AndResealsChecksum() {
    // 700 bytes → 3 sectors (768); 68 bytes of slack in the last sector.
    const int fileSize = 700;
    var content = new byte[fileSize];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)(i | 1);

    var w = new ZxSclWriter();
    w.AddFile("CODE.cod", content);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);

    // Simulate a foreign image with recoverable garbage in the slack.
    var dataOffset = DirectoryStart + 1 * HeaderSize;      // single entry
    var paddedEnd = dataOffset + 3 * SectorSize;
    ms.Position = dataOffset + fileSize;
    for (var i = dataOffset + fileSize; i < paddedEnd; i++)
      ms.WriteByte(0xAA);

    ms.Position = 0;
    var wiped = new ZxSclFormatDescriptor().WipeUnusedSpace(ms);

    Assert.That(wiped, Is.EqualTo(paddedEnd - (dataOffset + fileSize)),
      "exactly the sector slack must be wiped");

    var bytes = ms.ToArray();
    for (var i = dataOffset + fileSize; i < paddedEnd; i++)
      Assert.That(bytes[i], Is.Zero, $"slack byte at {i} must be zeroed");
    for (var i = 0; i < fileSize; i++)
      Assert.That(bytes[dataOffset + i], Is.EqualTo(content[i]), "live payload bytes must stay intact");

    // The trailing checksum must equal the little-endian 32-bit sum of all preceding bytes.
    var sum = 0u;
    for (var i = 0; i < bytes.Length - 4; i++) sum += bytes[i];
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)),
      Is.EqualTo(sum), "wipe must re-seal the trailing CRC");

    // And the image must still parse.
    using var r = new ZxSclReader(new MemoryStream(bytes));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
  }

  [Test]
  public void WipingPristineImage_LeavesBytesIdentical() {
    var w = new ZxSclWriter();
    w.AddFile("A.dat", "short payload"u8.ToArray());
    w.AddFile("B.cod", new byte[512]);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;
    new ZxSclFormatDescriptor().WipeUnusedSpace(ms);

    Assert.That(ms.ToArray(), Is.EqualTo(image),
      "a freshly written SCL has zero slack content — wipe must be a byte-level no-op");
  }
}
