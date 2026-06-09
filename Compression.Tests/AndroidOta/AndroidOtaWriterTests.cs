using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.AndroidOta;

namespace Compression.Tests.AndroidOta;

[TestFixture]
public class AndroidOtaWriterTests {

  [Test, Category("HappyPath")]
  public void Capabilities_IncludeWormCreate() {
    var d = new AndroidOtaFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_EmitsValidCrAuHeader() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("data.bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
    };

    using var output = new MemoryStream();
    new AndroidOtaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var bytes = output.ToArray();
    Assert.That(bytes[0], Is.EqualTo((byte)'C'));
    Assert.That(bytes[1], Is.EqualTo((byte)'r'));
    Assert.That(bytes[2], Is.EqualTo((byte)'A'));
    Assert.That(bytes[3], Is.EqualTo((byte)'U'));
    var version = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(4, 8));
    Assert.That(version, Is.EqualTo(2UL));
    var manifestSize = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12, 8));
    Assert.That(manifestSize, Is.GreaterThan(0UL));
    var sigSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
    Assert.That(sigSize, Is.EqualTo(0u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsThroughDescriptor() {
    var payloadData = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("data.bin", payloadData),
    };

    using var output = new MemoryStream();
    new AndroidOtaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var entries = new AndroidOtaFormatDescriptor().List(output, null);
    Assert.That(entries.Select(e => e.Name).ToList(), Has.Member("data.bin"));
    Assert.That(entries.First(e => e.Name == "data.bin").OriginalSize, Is.EqualTo(payloadData.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_PreservesExplicitManifestAndSignature() {
    var manifest = new byte[] { 0x08, 0x05, 0x10, 0x10 }; // arbitrary protobuf-looking bytes
    var signature = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
    var data = new byte[] { 0xF0, 0xF1, 0xF2 };
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("manifest.pb", manifest),
      ArchiveInputInfo.InMemory("metadata_signature.bin", signature),
      ArchiveInputInfo.InMemory("data.bin", data),
    };

    using var output = new MemoryStream();
    new AndroidOtaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var bytes = output.ToArray();
    var manifestSize = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12, 8));
    var sigSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
    Assert.That(manifestSize, Is.EqualTo((ulong)manifest.Length));
    Assert.That(sigSize, Is.EqualTo((uint)signature.Length));
    Assert.That(bytes.AsSpan(24, manifest.Length).ToArray(), Is.EqualTo(manifest));
    Assert.That(bytes.AsSpan(24 + manifest.Length, signature.Length).ToArray(), Is.EqualTo(signature));
    Assert.That(bytes.AsSpan(24 + manifest.Length + signature.Length, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_VersionOptionHonoured() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("data.bin", new byte[] { 1, 2, 3 }),
    };
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["ota_version"] = "5" },
    };

    using var output = new MemoryStream();
    new AndroidOtaFormatDescriptor().Create(output, inputs, options);

    var bytes = output.ToArray();
    var version = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(4, 8));
    Assert.That(version, Is.EqualTo(5UL));
  }

  [Test, Category("EdgeCase")]
  public void Create_EmptyInputs_ProducesValidHeader() {
    using var output = new MemoryStream();
    new AndroidOtaFormatDescriptor().Create(output, new List<ArchiveInputInfo>(), new FormatCreateOptions());

    var bytes = output.ToArray();
    Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(24));
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("CrAU"u8.ToArray()));

    using var ms = new MemoryStream(bytes);
    var entries = new AndroidOtaFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(5));
  }

  [Test, Category("EdgeCase")]
  public void Create_IgnoresFullBinAndMetadataIni() {
    // FULL.bin and metadata.ini are synthetic Extract outputs that should not
    // be embedded if the user feeds them back in (e.g. via a copy pipeline).
    var data = new byte[] { 0x77, 0x88 };
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("FULL.bin", new byte[] { 0xFF, 0xFF, 0xFF }),
      ArchiveInputInfo.InMemory("metadata.ini", new byte[] { 0xFE }),
      ArchiveInputInfo.InMemory("data.bin", data),
    };

    using var output = new MemoryStream();
    new AndroidOtaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    output.Position = 0;

    var entries = new AndroidOtaFormatDescriptor().List(output, null);
    Assert.That(entries.First(e => e.Name == "data.bin").OriginalSize, Is.EqualTo(data.Length));
  }
}
