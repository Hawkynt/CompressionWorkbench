#pragma warning disable CS0618
using System.Text;
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

  private static MemoryStream BuildNamespaceSnapshot(
      string rootPath,
      FilesystemSourceRole role,
      uint numericId,
      int formatVersion) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var prefix = rootPath.Replace('\\', '/').Trim('/');
    var idName = role == FilesystemSourceRole.Metadata ? "nodeNumID" : "targetNumID";
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory($"{prefix}/format.conf", Encoding.UTF8.GetBytes($"# BeeGFS target\nversion={formatVersion}\n")),
      ArchiveInputInfo.InMemory($"{prefix}/{idName}", Encoding.ASCII.GetBytes(numericId.ToString(System.Globalization.CultureInfo.InvariantCulture))),
    };
    if (role == FilesystemSourceRole.Metadata) {
      inputs.Add(ArchiveInputInfo.InMemory($"{prefix}/inodes/.keep", []));
      // A real content directory for the logical root. The #fSiDs# child is
      // deliberately ignored by logical enumeration but keeps the directory
      // hierarchy materialized in archive-derived backing sessions.
      inputs.Add(ArchiveInputInfo.InMemory($"{prefix}/dentries/00/00/root/#fSiDs#/.keep", []));
    } else {
      inputs.Add(ArchiveInputInfo.InMemory($"{prefix}/chunks/.keep", []));
    }

    var creator = FormatRegistry.GetById("Zip") as IArchiveCreatable
      ?? throw new InvalidOperationException("ZIP creator is not registered for BeeGFS multi-stream tests.");
    var image = new MemoryStream();
    creator.Create(image, inputs, new FormatCreateOptions());
    image.Position = 0;
    return image;
  }

  private static FilesystemStreamSet ValidTargetSet(
      out MemoryStream metadata,
      out MemoryStream storage,
      uint metadataId = 7,
      uint storageId = 101,
      int metadataVersion = 4,
      int storageVersion = 3) {
    metadata = BuildNamespaceSnapshot("targets/meta", FilesystemSourceRole.Metadata, metadataId, metadataVersion);
    storage = BuildNamespaceSnapshot("targets/storage", FilesystemSourceRole.Data, storageId, storageVersion);
    return new FilesystemStreamSet([
      new FilesystemStreamSource("meta-7", metadata, FilesystemSourceRole.Unknown, "Zip", "/targets/meta"),
      new FilesystemStreamSource("storage-101", storage, FilesystemSourceRole.Unknown, "Zip", "/targets/storage"),
    ]);
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
      Assert.That(descriptor, Is.InstanceOf<IMultiStreamFilesystemDriverProvider>());
      Assert.That(FilesystemSupportMatrix.State(descriptor), Is.EqualTo("N/A"),
        "The package matrix describes standalone stream/image state; BeeGFS R capability is multi-source.");
    });
  }

  [Test, Category("Regression")]
  public void Registry_UsesNativeFailClosedSingleStreamProfile_NotArchiveProjection() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    using var image = LegacyTaggedStream();

    var coverage = FormatRegistry.GetFilesystemDriverCoverage("BeeGfs");
    var profile = FormatRegistry.ProbeFilesystem("BeeGfs", image);

    Assert.Multiple(() => {
      Assert.That(FormatRegistry.GetArchiveOps("BeeGfs"), Is.Null);
      Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.DescriptorNative));
      Assert.That(coverage.HasArchiveProjection, Is.False);
      Assert.That(coverage.HasArchiveMutation, Is.False);
      Assert.That(coverage.HasMultiStreamProvider, Is.True);
      Assert.That(coverage.HasNativeReadinessProvider, Is.True);
      Assert.That(profile.Capabilities, Is.EqualTo(FilesystemDriverCapabilities.None));
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.CanMountWritable, Is.False);
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
      Assert.That(limitations, Does.Contain("FilesystemStreamSet"));
    });
  }

  [Test, Category("Regression")]
  public void LegacyReader_RejectsFormerSyntheticMagic() {
    using var image = LegacyTaggedStream();

    var error = Assert.Throws<NotSupportedException>(() => _ = new BeeGfsReader(image));

    Assert.That(error!.Message, Does.Contain("no standalone single-stream image"));
  }

  [TestCase(true)]
  [TestCase(false)]
  [Category("Exception")]
  public void OpenFilesystem_RejectsSingleStream(bool readOnly) {
    var descriptor = new BeeGfsFormatDescriptor();
    using var image = LegacyTaggedStream();

    var error = Assert.Throws<NotSupportedException>(
      () => descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: readOnly)));

    Assert.Multiple(() => {
      Assert.That(error!.Message, Does.Contain("one Stream"));
      Assert.That(error.Message, Does.Contain("FilesystemStreamSet"));
    });
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
      Assert.That(readOnlyBlockers, Does.Contain("metadata and storage targets"));
      Assert.That(readOnlyBlockers, Does.Contain("FilesystemStreamSet"));

      Assert.That(readWrite.Derivable, Is.False);
      Assert.That(readWrite.UsesNativeProvider, Is.True);
      Assert.That(readWrite.AvailableLayers, Is.EqualTo(FilesystemDriverReadinessLayer.None));
      Assert.That(readWrite.RequiredLayers.HasFlag(FilesystemDriverReadinessLayer.WriteData), Is.True);
      Assert.That(readWrite.RequiredLayers.HasFlag(FilesystemDriverReadinessLayer.DurabilityModel), Is.True);
      Assert.That(readWriteBlockers, Does.Contain("coordinated metadata"));
    });
  }

  [Test, Category("HappyPath")]
  public void MultiStream_EmptyRootTopologyIsMountableReadOnly() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var sources = ValidTargetSet(out var metadata, out var storage);
    using (metadata)
    using (storage) {
      var profile = FormatRegistry.ProbeFilesystem("BeeGfs", sources);
      var readiness = FormatRegistry.AssessFilesystemDriver("BeeGfs", sources, FilesystemDriverTarget.ReadOnly);

      Assert.Multiple(() => {
        Assert.That(profile.ProfileName, Does.Contain("BeeGFS offline V3/V6"));
        Assert.That(profile.CanMount, Is.True);
        Assert.That(profile.CanMountWritable, Is.False);
        Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.EnumerateDirectories), Is.True);
        Assert.That(readiness.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.ImageValidation), Is.True);
        Assert.That(readiness.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.Namespace), Is.True);
        Assert.That(readiness.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.NativeStableNodeIds), Is.True);
        Assert.That(readiness.Derivable, Is.True);
      });
    }
  }

  [Test, Category("HappyPath")]
  public void MultiStream_OpenExposesEmptyLogicalRootAndHonorsLeaveOpen() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var sources = ValidTargetSet(out var metadata, out var storage);
    using (metadata)
    using (storage) {
      using (var session = FormatRegistry.OpenFilesystem(
               "BeeGfs", sources, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true))) {
        Assert.That(session.Enumerate(session.RootNodeId), Is.Empty);
      }
      Assert.Multiple(() => {
        Assert.That(metadata.CanRead, Is.True);
        Assert.That(storage.CanRead, Is.True);
      });
    }
  }

  [Test, Category("Exception")]
  public void MultiStream_WritableOpenFailsClosed() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var sources = ValidTargetSet(out var metadata, out var storage);
    using (metadata)
    using (storage) {
      var error = Assert.Throws<NotSupportedException>(() =>
        FormatRegistry.OpenFilesystem("BeeGfs", sources, new FilesystemOpenOptions(ReadOnly: false)));
      Assert.That(error!.Message, Does.Contain("Writable BeeGFS"));
    }
  }

  [Test, Category("Exception")]
  public void MultiStream_RejectsDuplicateStorageTargetIds() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    using var metadata = BuildNamespaceSnapshot("meta", FilesystemSourceRole.Metadata, 7, 4);
    using var storageA = BuildNamespaceSnapshot("storage-a", FilesystemSourceRole.Data, 101, 3);
    using var storageB = BuildNamespaceSnapshot("storage-b", FilesystemSourceRole.Data, 101, 3);
    var sources = new FilesystemStreamSet([
      new FilesystemStreamSource("meta", metadata, FilesystemSourceRole.Metadata, "Zip", "/meta"),
      new FilesystemStreamSource("storage-a", storageA, FilesystemSourceRole.Data, "Zip", "/storage-a"),
      new FilesystemStreamSource("storage-b", storageB, FilesystemSourceRole.Data, "Zip", "/storage-b"),
    ]);

    var profile = FormatRegistry.ProbeFilesystem("BeeGfs", sources);

    Assert.Multiple(() => {
      Assert.That(profile.ProfileName, Does.Contain("invalid or unsupported"));
      Assert.That(string.Join('\n', profile.Limitations), Does.Contain("duplicate storage targetNumID 101"));
    });
  }

  [Test, Category("Exception")]
  public void MultiStream_RejectsUnsupportedTargetFormatVersion() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var sources = ValidTargetSet(
      out var metadata,
      out var storage,
      metadataVersion: 5);
    using (metadata)
    using (storage) {
      var profile = FormatRegistry.ProbeFilesystem("BeeGfs", sources);
      Assert.That(string.Join('\n', profile.Limitations), Does.Contain("supported versions are 3..4"));
    }
  }

  [Test, Category("Exception")]
  public void FilesystemStreamSet_RejectsDuplicateMemberNames() {
    using var first = new MemoryStream();
    using var second = new MemoryStream();

    var error = Assert.Throws<ArgumentException>(() => _ = new FilesystemStreamSet([
      new FilesystemStreamSource("target", first),
      new FilesystemStreamSource("TARGET", second),
    ]));

    Assert.That(error!.Message, Does.Contain("Duplicate filesystem stream source name"));
  }

  [Test, Category("HappyPath")]
  public void Description_StatesMultiStreamReadSubset() {
    var description = new BeeGfsFormatDescriptor().Description;

    Assert.Multiple(() => {
      Assert.That(description, Does.Contain("distributed filesystem"));
      Assert.That(description, Does.Contain("no standalone byte-stream image"));
      Assert.That(description, Does.Contain("FilesystemStreamSet"));
      Assert.That(description, Does.Contain("read-only"));
      Assert.That(description, Does.Contain("RAID0"));
    });
  }
}
