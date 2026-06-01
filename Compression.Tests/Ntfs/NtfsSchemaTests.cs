using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

[TestFixture]
public class NtfsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesNonEmptyOptionsSchema() {
    var descriptor = new NtfsFormatDescriptor();
    Assert.That(descriptor, Is.AssignableTo<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)descriptor).OptionsSchema;
    Assert.That(schema, Is.Not.Empty);

    var keys = schema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("ClusterSize"));
    Assert.That(keys, Does.Contain("VolumeLabel"));
    // The upstream writer exposes the MFT *record size* knob (per-record bytes),
    // not the MFT *reserve %* knob the stash schema would have published — the
    // writer's MFT zone is currently a fixed 12.5 % reservation. MftReserve %
    // wiring is a deferred TODO. See ImageSize for the volume-size knob.
    Assert.That(keys, Does.Contain("MftRecordSize"));
    Assert.That(keys, Does.Contain("ImageSize"));
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_FromFormatSpecific_RoundTrips() {
    // Verify the schema knob actually flows through to the writer: a non-default
    // VolumeLabel value must end up encoded in the image's $Volume $VOLUME_NAME attribute.
    var descriptor = new NtfsFormatDescriptor();
    var output = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "SmokeTest" }
    };

    descriptor.Create(output, [], options);

    output.Position = 0;
    // The label is written as UTF-16LE into the resident $VOLUME_NAME attribute.
    // We grep for that pattern in the resulting image.
    var image = output.ToArray();
    var labelUtf16 = System.Text.Encoding.Unicode.GetBytes("SmokeTest");
    var found = ContainsSubsequence(image, labelUtf16);
    Assert.That(found, Is.True, "Custom volume label should appear in the produced NTFS image.");
  }

  private static bool ContainsSubsequence(byte[] haystack, byte[] needle) {
    if (needle.Length == 0 || needle.Length > haystack.Length) return false;
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return true;
    }
    return false;
  }
}
