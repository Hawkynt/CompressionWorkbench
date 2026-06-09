#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Wbn;

namespace Compression.Tests.Wbn;

[TestFixture]
public class WbnWriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsThroughReader() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("https://example.test/", Encoding.UTF8.GetBytes("<html>root</html>")),
      ArchiveInputInfo.InMemory("https://example.test/styles.css", Encoding.UTF8.GetBytes("body{color:red}")),
    };

    using var output = new MemoryStream();
    new WbnFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var reader = new WbnReader(output);
    Assert.That(reader.MagicOk, Is.True);
    Assert.That(reader.Version, Does.StartWith("b2"));
    Assert.That(reader.ResourceCount, Is.EqualTo(2));
    Assert.That(reader.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Create_StartsWithCanonicalMagic() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("https://example.test/", new byte[] { 1, 2, 3 }),
    };

    using var output = new MemoryStream();
    new WbnFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var bytes = output.ToArray();
    Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(10));
    var magic = bytes.AsSpan(0, 10).ToArray();
    Assert.That(magic, Is.EqualTo(WbnConstants.Magic));
  }

  [Test, Category("HappyPath")]
  public void Create_DefaultPrimaryUrl_TakenFromFirstInput() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("https://first.example/", new byte[] { 1 }),
      ArchiveInputInfo.InMemory("https://second.example/", new byte[] { 2 }),
    };

    using var output = new MemoryStream();
    new WbnFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var reader = new WbnReader(output);
    Assert.That(reader.PrimaryUrl, Is.EqualTo("https://first.example/"));
  }

  [Test, Category("HappyPath")]
  public void Create_ExplicitPrimaryUrlOverride() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("https://res.example/data", new byte[] { 1 }),
    };
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["wbn_primary_url"] = "https://override.example/landing",
      },
    };

    using var output = new MemoryStream();
    new WbnFormatDescriptor().Create(output, inputs, options);

    output.Position = 0;
    var reader = new WbnReader(output);
    Assert.That(reader.PrimaryUrl, Is.EqualTo("https://override.example/landing"));
  }

  [Test, Category("EdgeCase")]
  public void Create_EmptyInputs_ProducesValidMagicAndZeroResources() {
    using var output = new MemoryStream();
    new WbnFormatDescriptor().Create(output, new List<ArchiveInputInfo>(), new FormatCreateOptions());

    output.Position = 0;
    var reader = new WbnReader(output);
    Assert.That(reader.MagicOk, Is.True);
    Assert.That(reader.ResourceCount, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Create_ManyResources_AllSurfacedInIndex() {
    var inputs = new List<ArchiveInputInfo>();
    for (var i = 0; i < 25; i++)
      inputs.Add(ArchiveInputInfo.InMemory($"https://example.test/r{i}", new byte[] { (byte)i }));

    using var output = new MemoryStream();
    new WbnFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var reader = new WbnReader(output);
    Assert.That(reader.ResourceCount, Is.EqualTo(25));
  }
}
