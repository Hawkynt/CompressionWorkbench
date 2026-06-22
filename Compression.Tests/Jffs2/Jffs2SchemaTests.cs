using Compression.Registry;
using FileSystem.Jffs2;

namespace Compression.Tests.Jffs2;

/// <summary>
/// Schema-wiring coverage for <see cref="Jffs2FormatDescriptor"/>: verifies the
/// published EraseBlockSize knob and that Create() pads the image to a whole
/// multiple of the chosen erase block, with file round-trip.
/// </summary>
[TestFixture]
public class Jffs2SchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesEraseBlockSizeSchema() {
    var desc = new Jffs2FormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("EraseBlockSize"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultEraseBlockSize_PadsImageAndRoundTrips() {
    var desc = new Jffs2FormatDescriptor();
    var payload = "hello jffs2"u8.ToArray();

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["EraseBlockSize"] = "8 KB" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("greeting.txt", payload)], opts);
    var image = ms.ToArray();

    // The default erase block is 128 KiB; an 8 KiB image proves the knob took
    // effect (it is a whole multiple of 8 KiB and far below the default).
    Assert.That(image.Length, Is.EqualTo(8 * 1024), "image must be padded to one 8 KiB erase block.");
    Assert.That(image.Length % (8 * 1024), Is.EqualTo(0));

    using var rs = new MemoryStream(image);
    var back = desc.ExtractEntryToMemory(rs, "greeting.txt", null);
    Assert.That(back, Is.EqualTo(payload));
  }
}
