using System.Text;
using Compression.Registry;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DriveSpace;

[TestFixture]
public class DriveSpaceSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesTheGenuineCodecChoice_WithJmByDefault() {
    var schema = ((IFormatOptionsSchema)new DriveSpaceFormatDescriptor()).OptionsSchema;
    var method = schema.Single(o => o.Key == "Method");

    Assert.Multiple(() => {
      Assert.That(method.AllowedValues, Is.EquivalentTo(new[] { "Stored", "DS", "JM", "Auto" }));
      Assert.That(method.Default, Is.EqualTo("JM"));
      Assert.That(schema.Any(o => o.Key == "Compatibility"), Is.False, "one layout only: no layout choice");
    });
  }

  private static byte[] CompressiblePayload() {
    var phrase = "DriveSpace JM packs sectors in 8 KiB clusters; repeated phrases compress hard. ";
    var sb = new StringBuilder(phrase.Length * 400);
    for (var i = 0; i < 400; ++i) sb.Append(phrase);
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static byte[] CreateCvf(FormatCreateOptions options, byte[] data) {
    using var ms = new MemoryStream();
    ((IArchiveCreatable)new DriveSpaceFormatDescriptor()).Create(ms, [ArchiveInputInfo.InMemory("DATA.BIN", data)], options);
    return ms.ToArray();
  }

  private static byte[] Extract(byte[] cvf) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb-drvsp-{Guid.NewGuid():N}");
    try {
      new DriveSpaceFormatDescriptor().Extract(new MemoryStream(cvf), dir, null, null);
      return File.ReadAllBytes(Path.Combine(dir, "DATA.BIN"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void GivenTheMethodOption_WhenCreated_ThenItDrivesTheCodecAndRoundTrips() {
    var payload = CompressiblePayload();
    var stored = CreateCvf(new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Method"] = "Stored" } }, payload);
    var jm = CreateCvf(new FormatCreateOptions(), payload);

    Assert.Multiple(() => {
      // The volume has a fixed minimum size, so the codec shows in the cluster payloads, not the length.
      Assert.That(jm.AsSpan().IndexOf("JM\0\0"u8), Is.GreaterThan(0), "JM clusters carry their method header");
      Assert.That(stored.AsSpan().IndexOf("JM\0\0"u8), Is.LessThan(0), "stored clusters carry none");
      Assert.That(Extract(stored), Is.EqualTo(payload));
      Assert.That(Extract(jm), Is.EqualTo(payload));
      Assert.That(Encoding.ASCII.GetString(jm, 3, 8), Is.EqualTo("MSDBL6.0"), "the real container");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenATieredMethodName_WhenCreated_ThenItRoundTrips([Values("jm", "jm+", "jm++", "ds++", "auto+", "stored")] string method) {
    var payload = CompressiblePayload();

    Assert.That(Extract(CreateCvf(new FormatCreateOptions { MethodName = method }, payload)), Is.EqualTo(payload));
  }

  [Test, Category("Sad")]
  public void GivenAMethodTheVolumeCannotRecord_WhenCreated_ThenItIsRefused([Values("ds-lz77", "sq", "sd4")] string method)
    => Assert.That(() => CreateCvf(new FormatCreateOptions { MethodName = method }, [1, 2, 3]), Throws.InstanceOf<ArgumentException>());
}
