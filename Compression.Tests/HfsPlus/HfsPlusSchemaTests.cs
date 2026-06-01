using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

[TestFixture]
public class HfsPlusSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesNonEmptyOptionsSchema() {
    var descriptor = new HfsPlusFormatDescriptor();
    Assert.That(descriptor, Is.AssignableTo<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)descriptor).OptionsSchema;
    Assert.That(schema, Is.Not.Empty);

    var keys = schema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("CaseSensitive"));
    Assert.That(keys, Does.Contain("Journal"));
    Assert.That(keys, Does.Contain("JournalSize"));
    Assert.That(keys, Does.Contain("VolumeLabel"));

    // JournalSize must declare DependsOn=Journal=true so the UI hides it when
    // journaling is off.
    var journalSize = schema.First(o => o.Key == "JournalSize");
    Assert.That(journalSize.DependsOn, Is.EqualTo("Journal=true"));
  }

  [Test, Category("HappyPath")]
  public void Create_CaseSensitive_ProducesHxSignature() {
    var descriptor = new HfsPlusFormatDescriptor();

    var hfsxStream = new MemoryStream();
    descriptor.Create(hfsxStream, [], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["CaseSensitive"] = "true" }
    });

    var plusStream = new MemoryStream();
    descriptor.Create(plusStream, [], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["CaseSensitive"] = "false" }
    });

    // Volume header lives at offset 1024 and starts with a 2-byte big-endian signature.
    var hfsxImage = hfsxStream.ToArray();
    var plusImage = plusStream.ToArray();
    var hfsxSig = BinaryPrimitives.ReadUInt16BigEndian(hfsxImage.AsSpan(1024, 2));
    var plusSig = BinaryPrimitives.ReadUInt16BigEndian(plusImage.AsSpan(1024, 2));

    Assert.That(hfsxSig, Is.EqualTo(0x4858), "CaseSensitive=true should emit HFSX 'HX' signature.");
    Assert.That(plusSig, Is.EqualTo(0x482B), "CaseSensitive=false should emit HFS+ 'H+' signature.");
  }
}
