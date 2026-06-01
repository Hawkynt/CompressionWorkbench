using Compression.Registry;
using FileSystem.Btrfs;

namespace Compression.Tests.Btrfs;

[TestFixture]
public class BtrfsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesNonEmptyOptionsSchema() {
    var descriptor = new BtrfsFormatDescriptor();
    Assert.That(descriptor, Is.AssignableTo<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)descriptor).OptionsSchema;
    Assert.That(schema, Is.Not.Empty);

    var keys = schema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("NodeSize"));
    Assert.That(keys, Does.Contain("SectorSize"));
    Assert.That(keys, Does.Contain("Label"));
    Assert.That(keys, Does.Contain("Features"));

    var sector = schema.First(o => o.Key == "SectorSize");
    Assert.That(sector.AllowedValues, Is.EquivalentTo(new[] { "4096" }));
  }
}
