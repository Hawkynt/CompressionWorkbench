#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Hammer2;

namespace Compression.Tests.Hammer2;

/// <summary>
/// HAMMER2 lays a volume out again by moving blocks. A blockref names its block
/// by a device offset, and the check beside it covers the bytes it points at —
/// which a move does not change.
/// </summary>
/// <remarks>
/// What a move does change is every check above: the blockref sits inside its
/// parent block, whose check sits in the blockref naming the parent, up to the
/// volume headers and their sector CRCs. So the pass rewrites that chain
/// outwards for each block it moves, and stamps the headers at the end.
/// </remarks>
[TestFixture]
public class Hammer2PlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_h2_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 4; ++k) {
        var data = Payload(k, 20000 + k * 9000);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new Hammer2FormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  private static Dictionary<string, byte[]> ReadBack(MemoryStream image) {
    image.Position = 0;
    using var reader = new Hammer2Reader(image);
    var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var file in reader.EnumerateFiles()) {
      using var buffer = new MemoryStream();
      reader.ExtractTo(file, buffer);
      contents[Path.GetFileName(file.Path)] = buffer.ToArray();
    }

    return contents;
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheVolumesSize(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new Hammer2FormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    var read = ReadBack(image);
    foreach (var (name, data) in files) {
      Assert.That(read.Keys, Does.Contain(name), $"{name} must still be on the volume");
      Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_ActuallyMovesTheBlocks() {
    using var image = Volume(out _);
    image.Position = 0;
    var descriptor = new Hammer2FormatDescriptor();
    var before = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();
    Assert.That(before, Is.Not.Empty, "the probe volume must have data blocks to move");

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var after = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();

    Assert.That(after, Is.Not.EqualTo(before), "packing against the tail must move something");
    Assert.That(after.Max(), Is.GreaterThan(before.Max()), "and it must move towards the tail");
  }

  [Test]
  public void Defragment_LeavesTheVolumeHeadersCheckingOut() {
    using var image = Volume(out _);
    image.Position = 0;
    new Hammer2FormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    // A header carries a CRC over its first sector, one over its second, and
    // one over the whole 64 KB. All three are taken again after a pass; a
    // volume whose header does not check out is one a driver will not mount.
    var header = new byte[65536];
    image.Position = 0;
    image.ReadExactly(header);

    var sector1 = Hammer2CrcProbe.Iscsi32(header.AsSpan(512, 512));
    var sector0Field = BitConverter.ToUInt32(header, 0x1E0 + 7 * 4);
    var sector1Field = BitConverter.ToUInt32(header, 0x1E0 + 6 * 4);
    var wholeField = BitConverter.ToUInt32(header, 65536 - 4);

    Assert.Multiple(() => {
      Assert.That(sector1Field, Is.EqualTo(sector1), "the second sector's CRC must hold");
      Assert.That(sector0Field, Is.EqualTo(Hammer2CrcProbe.Iscsi32(header.AsSpan(0, 512 - 4))),
        "the first sector's CRC must hold");
      Assert.That(wholeField, Is.EqualTo(Hammer2CrcProbe.Iscsi32(header.AsSpan(0, 65536 - 4))),
        "the CRC over the whole header must hold");
    });
  }
}

/// <summary>
/// The CRC-32C HAMMER2 stamps its headers with, so the test can check them
/// without reaching into the implementation.
/// </summary>
internal static class Hammer2CrcProbe {
  private static readonly uint[] Table = Build();

  private static uint[] Build() {
    var table = new uint[256];
    for (var n = 0u; n < 256; ++n) {
      var c = n;
      for (var k = 0; k < 8; ++k) c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
      table[n] = c;
    }

    return table;
  }

  public static uint Iscsi32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }
}
