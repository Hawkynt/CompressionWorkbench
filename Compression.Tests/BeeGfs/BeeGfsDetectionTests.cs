using Compression.Registry;
using Compression.Tests.Documentation;
using FileSystem.BeeGfs;

namespace Compression.Tests.BeeGfs;

[TestFixture]
public class BeeGfsDetectionTests {

  private static MemoryStream LegacyTaggedStream() {
    var image = new byte[128];
    "BeeGFS"u8.CopyTo(image);
    return new MemoryStream(image, writable: false);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DoesNotInventStandaloneStreamFormat() {
    var descriptor = new BeeGfsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("BeeGfs"));
      Assert.That(descriptor.Capabilities, Is.EqualTo(FormatCapabilities.None));
      Assert.That(descriptor.DefaultExtension, Is.Empty);
      Assert.That(descriptor.Extensions, Is.Empty);
      Assert.That(descriptor.MagicSignatures, Is.Empty);
      Assert.That(descriptor.Methods, Is.Empty);
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveFormatOperations>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(FilesystemSupportMatrix.State(descriptor), Is.EqualTo("N/A"));
    });
  }

  [Test, Category("Regression")]
  public void LegacySyntheticMagic_DoesNotProduceMountableFilesystem() {
    var descriptor = new BeeGfsFormatDescriptor();
    using var image = LegacyTaggedStream();

    var profile = descriptor.ProbeFilesystem(image);
    var limitations = string.Join('\n', profile.Limitations);

    Assert.Multiple(() => {
      Assert.That(profile.FormatId, Is.EqualTo(descriptor.Id));
      Assert.That(profile.Capabilities, Is.EqualTo(FilesystemDriverCapabilities.None));
      Assert.That(profile.MutationModel, Is.EqualTo(FilesystemMutationModel.None));
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(limitations, Does.Contain("single-stream"));
      Assert.That(limitations, Does.Contain("ext4").And.Contain("XFS"));
    });
  }

  [TestCase(true)]
  [TestCase(false)]
  [Category("Exception")]
  public void OpenFilesystem_RejectsSingleStream(bool readOnly) {
    var descriptor = new BeeGfsFormatDescriptor();
    using var image = LegacyTaggedStream();

    var error = Assert.Throws<NotSupportedException>(
      () => descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: readOnly)));

    Assert.That(error!.Message, Does.Contain("one Stream"));
  }

  [Test, Category("HappyPath")]
  public void Readiness_ExplainsDistributedInputBoundary() {
    var descriptor = new BeeGfsFormatDescriptor();
    using var image = LegacyTaggedStream();

    var readOnly = descriptor.DescribeFilesystemDriverReadiness(image, FilesystemDriverTarget.ReadOnly);
    var readWrite = descriptor.DescribeFilesystemDriverReadiness(image, FilesystemDriverTarget.ReadWrite);
    var readOnlyBlockers = string.Join('\n', readOnly.Blockers);
    var readWriteBlockers = string.Join('\n', readWrite.Blockers);

    Assert.Multiple(() => {
      Assert.That(readOnly.Derivable, Is.False);
      Assert.That(readOnly.UsesNativeProvider, Is.True);
      Assert.That(readOnly.AvailableLayers, Is.EqualTo(FilesystemDriverReadinessLayer.None));
      Assert.That(readOnly.RequiredLayers.HasFlag(FilesystemDriverReadinessLayer.Namespace), Is.True);
      Assert.That(readOnlyBlockers, Does.Contain("metadata/storage targets"));
      Assert.That(readOnlyBlockers, Does.Contain("target and stripe mappings"));

      Assert.That(readWrite.Derivable, Is.False);
      Assert.That(readWrite.UsesNativeProvider, Is.True);
      Assert.That(readWrite.AvailableLayers, Is.EqualTo(FilesystemDriverReadinessLayer.None));
      Assert.That(readWrite.RequiredLayers.HasFlag(FilesystemDriverReadinessLayer.WriteData), Is.True);
      Assert.That(readWrite.RequiredLayers.HasFlag(FilesystemDriverReadinessLayer.DurabilityModel), Is.True);
      Assert.That(readWriteBlockers, Does.Contain("coordinated metadata"));
    });
  }

  [Test, Category("HappyPath")]
  public void Description_StatesThereIsNoStandaloneImage() {
    var description = new BeeGfsFormatDescriptor().Description;

    Assert.Multiple(() => {
      Assert.That(description, Does.Contain("distributed filesystem"));
      Assert.That(description, Does.Contain("no standalone byte-stream image"));
      Assert.That(description, Does.Contain("multi-target snapshot"));
    });
  }
}
