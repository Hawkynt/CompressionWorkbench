using Compression.Lib;
using FileFormat.Aiff;
using FileFormat.Au;
using FileFormat.Mp3;
using FileFormat.Wav;
using FileFormat.WavPack;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AudioConversionInventoryTests {

  [TestCase(typeof(WavFormatDescriptor), "pcm", "ima-adpcm", "ms-adpcm")]
  [TestCase(typeof(AiffFormatDescriptor), "pcm", "ima4", "alaw")]
  [TestCase(typeof(AuFormatDescriptor), "pcm", "mulaw", "alaw")]
  [TestCase(typeof(Mp3FormatDescriptor), "mp3", null, null)]
  [TestCase(typeof(WavPackFormatDescriptor), "wavpack", null, null)]
  public void AdaptedFormatsAdvertiseRealEncodeCapabilities(Type descriptorType, string codec1, string? codec2, string? codec3) {
    var descriptor = (Compression.Registry.IFormatDescriptor)Activator.CreateInstance(descriptorType)!;
    var capability = AudioConversionInventory.Describe(descriptor);

    Assert.Multiple(() => {
      Assert.That(capability.CanDecodePcm, Is.True);
      Assert.That(capability.CanEncodePcm, Is.True);
      Assert.That(capability.EncodeCodecs, Does.Contain(codec1));
      if (codec2 is not null) Assert.That(capability.EncodeCodecs, Does.Contain(codec2));
      if (codec3 is not null) Assert.That(capability.EncodeCodecs, Does.Contain(codec3));
    });
  }
}
