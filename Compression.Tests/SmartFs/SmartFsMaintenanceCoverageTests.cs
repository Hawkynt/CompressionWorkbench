#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.SmartFs;

namespace Compression.Tests.SmartFs;

[TestFixture]
public sealed class SmartFsMaintenanceCoverageTests {

  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.Ordinal) { "FULL.smartfs", "metadata.ini" };

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesEveryMaintenanceVerb() {
    var descriptor = new SmartFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.InstanceOf<IFormatOptionsSchema>());
    });
  }

  [Test, Category("HappyPath")]
  public void Writer_RoundTripsEverySpecifiedSectorSize() {
    foreach (var sectorSize in new[] { 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 }) {
      var writer = new SmartFsWriter { SectorSize = sectorSize };
      writer.AddFile("A.BIN", Enumerable.Range(0, 700).Select(static i => (byte)i).ToArray());

      using var image = new MemoryStream(writer.Build());
      using var reader = new SmartFsReader(image);
      var entry = reader.Entries.Single(e => e.Name == "A.BIN");

      Assert.Multiple(() => {
        Assert.That(reader.SectorSize, Is.EqualTo((uint)sectorSize), $"sector size {sectorSize}");
        Assert.That(reader.Extract(entry).Length, Is.EqualTo(700), $"sector size {sectorSize}");
      });
    }
  }

  [Test, Category("HappyPath")]
  public void Modify_AddReplaceRemove_PreservesSectorSize() {
    var descriptor = new SmartFsFormatDescriptor();
    using var image = WritableImage(BuildPaddedImage(2048, 64, ("A.TXT", "old"u8.ToArray())));

    descriptor.Add(image, [ArchiveInputInfo.InMemory("B.TXT", "new"u8.ToArray())]);
    descriptor.Add(image, [ArchiveInputInfo.InMemory("A.TXT", "replacement"u8.ToArray())]);
    descriptor.Remove(image, ["B.TXT"]);

    image.Position = 0;
    using var reader = new SmartFsReader(image);
    var real = reader.Entries.Where(IsReal).ToArray();

    Assert.Multiple(() => {
      Assert.That(reader.SectorSize, Is.EqualTo(2048u));
      Assert.That(real.Select(static e => e.Name), Is.EqualTo(new[] { "A.TXT" }));
      Assert.That(reader.Extract(real[0]), Is.EqualTo("replacement"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void Purge_LeavesSameSizedValidEmptyVolume() {
    var descriptor = new SmartFsFormatDescriptor();
    using var image = WritableImage(BuildPaddedImage(1024, 80, ("SECRET.BIN", new byte[5000])));
    var originalLength = image.Length;

    descriptor.Purge(image);

    image.Position = 0;
    using var reader = new SmartFsReader(image);
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(originalLength));
      Assert.That(reader.SectorSize, Is.EqualTo(1024u));
      Assert.That(reader.Entries.Where(IsReal), Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void Shrink_PreservesSectorSizeAndPayload() {
    var descriptor = new SmartFsFormatDescriptor();
    var payload = Enumerable.Range(0, 4097).Select(static i => (byte)(i * 17)).ToArray();
    using var input = WritableImage(BuildPaddedImage(2048, 128, ("DATA.BIN", payload)));
    using var output = new MemoryStream();

    descriptor.Shrink(input, output);

    Assert.That(output.Length, Is.LessThan(input.Length));
    output.Position = 0;
    using var reader = new SmartFsReader(output);
    var entry = reader.Entries.Single(e => e.Name == "DATA.BIN");
    Assert.Multiple(() => {
      Assert.That(reader.SectorSize, Is.EqualTo(2048u));
      Assert.That(reader.Extract(entry), Is.EqualTo(payload));
    });
  }

  [Test, Category("HappyPath")]
  public void Optimize_SelectsAndAppliesSmallestProjectedGeometry() {
    var descriptor = new SmartFsFormatDescriptor();
    using var source = WritableImage(BuildPaddedImage(4096, 32,
      ("A", new byte[] { 1 }), ("B", new byte[] { 2 }), ("C", new byte[] { 3 })));

    var analysis = descriptor.AnalyzeLayout(source);
    Assert.That(analysis.OptimalUnitSize, Is.EqualTo(256));

    source.Position = 0;
    using var target = new MemoryStream();
    descriptor.RebuildStreaming(source, target, new LayoutRebuildOptions { UnitSize = analysis.OptimalUnitSize });

    target.Position = 0;
    using var reader = new SmartFsReader(target);
    Assert.Multiple(() => {
      Assert.That(reader.SectorSize, Is.EqualTo(256u));
      Assert.That(reader.Entries.Where(IsReal).Select(static e => e.Name),
        Is.EquivalentTo(new[] { "A", "B", "C" }));
      Assert.That(target.Length, Is.LessThan(source.Length));
    });
  }

  private static byte[] BuildPaddedImage(
      int sectorSize,
      int totalSectors,
      params (string Name, byte[] Data)[] files) {
    var writer = new SmartFsWriter { SectorSize = sectorSize };
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    return writer.Build(totalSectors);
  }

  private static MemoryStream WritableImage(byte[] bytes) {
    var result = new MemoryStream();
    result.Write(bytes);
    result.Position = 0;
    return result;
  }

  private static bool IsReal(SmartFsEntry entry)
    => !entry.IsDirectory && !SyntheticNames.Contains(entry.Name);
}
