using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Schema-knob contract tests for <see cref="BcacheFsFormatDescriptor"/>: proves
/// the published <c>VolumeLabel</c> and <c>ImageSize</c> options are real knobs
/// the WORM superblock writer honours and the superblock reads back.
/// </summary>
[TestFixture]
public class BcacheFsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelAndImageSizeSchema() {
    var d = new BcacheFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "ImageSize"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabelAndImageSize_TakeEffect() {
    var d = new BcacheFsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["VolumeLabel"] = "bch-knob",
          ["ImageSize"] = "256 MB",
        },
      });

    var bytes = ms.ToArray();
    Assert.That(bytes.LongLength, Is.EqualTo(256L * 1024 * 1024), "ImageSize must size the image.");
    Assert.That(ReadLabel(bytes), Is.EqualTo("bch-knob"), "VolumeLabel must land in the superblock label[].");
  }

  // Primary bch_sb lives at sector 8 (byte 4096); label[32] is at struct offset 72.
  private static string ReadLabel(byte[] image) {
    var labelSpan = image.AsSpan(4096 + 72, 32);
    var len = labelSpan.IndexOf((byte)0);
    if (len < 0) len = 32;
    return len == 0 ? "" : Encoding.UTF8.GetString(labelSpan[..len]);
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultImageSize_IsMinimum() {
    var d = new BcacheFsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new FormatCreateOptions());
    Assert.That(ms.ToArray().LongLength, Is.EqualTo(BcacheFsWriter.MinImageSize));
  }
}
