using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class FilesystemMountCapabilityResolverTests {
  private static readonly FilesystemDriverCapabilities AllCoreCapabilities =
    FilesystemMountCapabilityResolver.CoreReadCapabilities |
    FilesystemMountCapabilityResolver.CoreWriteCapabilities;

  [Test]
  public void DescriptorCanModifyAloneDoesNotGrantWritableMount() {
    var descriptorCapabilities = FormatCapabilities.CanModify;
    Assert.That(descriptorCapabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var plan = Resolve(
      Profile(canMountWritable: false),
      MountAccessMode.ReadWrite,
      sourceCanWrite: true
    );

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.Reasons.Select(static reason => reason.Code),
        Does.Contain(MountSupportReasonCode.FilesystemProfileNotWritable));
      Assert.That(plan.Reasons.Select(static reason => reason.Code),
        Does.Not.Contain(MountSupportReasonCode.MissingDriverCapabilities));
    });
  }

  [Test]
  public void WholeImageRebuildNeverGrantsWritableMount() {
    var plan = Resolve(
      Profile(mutationModel: FilesystemMutationModel.WholeImageRebuild),
      MountAccessMode.ReadWrite,
      sourceCanWrite: true
    );

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.Reasons.Select(static reason => reason.Code),
        Does.Contain(MountSupportReasonCode.UnsupportedMutationModel));
    });
  }

  [Test]
  public void ReadOnlySourceRejectsWritableMount() {
    var plan = Resolve(Profile(), MountAccessMode.ReadWrite, sourceCanWrite: false);

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.Reasons.Select(static reason => reason.Code),
        Does.Contain(MountSupportReasonCode.SourceIsReadOnly));
    });
  }

  [TestCase(FilesystemDriverCapabilities.WriteData)]
  [TestCase(FilesystemDriverCapabilities.Truncate)]
  [TestCase(FilesystemDriverCapabilities.CreateFile)]
  [TestCase(FilesystemDriverCapabilities.DeleteFile)]
  [TestCase(FilesystemDriverCapabilities.CreateDirectory)]
  [TestCase(FilesystemDriverCapabilities.RemoveDirectory)]
  [TestCase(FilesystemDriverCapabilities.Rename)]
  [TestCase(FilesystemDriverCapabilities.Flush)]
  public void MissingCoreWritePrimitiveRejectsWritableMount(FilesystemDriverCapabilities missingCapability) {
    var plan = Resolve(
      Profile(capabilities: AllCoreCapabilities & ~missingCapability),
      MountAccessMode.ReadWrite,
      sourceCanWrite: true
    );

    var missingReason = plan.Reasons.Single(reason =>
      reason.Code == MountSupportReasonCode.MissingDriverCapabilities &&
      reason.MissingCapabilities.HasFlag(missingCapability));

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.MissingCapabilities, Is.EqualTo(missingCapability));
      Assert.That(missingReason.MissingCapabilities, Is.EqualTo(missingCapability));
    });
  }

  [TestCase(FilesystemDriverCapabilities.EnumerateDirectories)]
  [TestCase(FilesystemDriverCapabilities.ReadData)]
  [TestCase(FilesystemDriverCapabilities.RandomAccess)]
  [TestCase(FilesystemDriverCapabilities.StableNodeIds)]
  public void MissingCoreReadPrimitiveRejectsReadOnlyMount(FilesystemDriverCapabilities missingCapability) {
    var plan = Resolve(
      Profile(capabilities: AllCoreCapabilities & ~missingCapability),
      MountAccessMode.ReadOnly,
      sourceCanWrite: false
    );

    var missingReason = plan.Reasons.Single(reason => reason.Code == MountSupportReasonCode.MissingDriverCapabilities);

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.MissingCapabilities, Is.EqualTo(missingCapability));
      Assert.That(missingReason.MissingCapabilities, Is.EqualTo(missingCapability));
    });
  }

  [TestCase(MountAccessMode.ReadOnly)]
  [TestCase(MountAccessMode.ReadWrite)]
  public void UnavailableBackendRejectsEveryAccessMode(MountAccessMode accessMode) {
    var plan = Resolve(
      Profile(),
      accessMode,
      sourceCanWrite: true,
      backend: Backend(isAvailable: false)
    );

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.Reasons.Select(static reason => reason.Code),
        Does.Contain(MountSupportReasonCode.BackendUnavailable));
    });
  }

  [Test]
  public void BackendReadOnlySupportFlagIsRequired() {
    var plan = Resolve(
      Profile(),
      MountAccessMode.ReadOnly,
      sourceCanWrite: false,
      backend: Backend(supportsReadOnly: false)
    );

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.Reasons.Select(static reason => reason.Code),
        Does.Contain(MountSupportReasonCode.BackendDoesNotSupportReadOnly));
    });
  }

  [Test]
  public void BackendReadWriteSupportFlagIsRequired() {
    var plan = Resolve(
      Profile(),
      MountAccessMode.ReadWrite,
      sourceCanWrite: true,
      backend: Backend(supportsReadWrite: false)
    );

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.Reasons.Select(static reason => reason.Code),
        Does.Contain(MountSupportReasonCode.BackendDoesNotSupportReadWrite));
    });
  }

  [Test]
  public void OptionalFilesystemCapabilitiesRemainOptional() {
    var optional =
      FilesystemDriverCapabilities.HardLinks |
      FilesystemDriverCapabilities.SymbolicLinks |
      FilesystemDriverCapabilities.SetMetadata |
      FilesystemDriverCapabilities.SparseFiles |
      FilesystemDriverCapabilities.Transactions;
    var profile = Profile(capabilities: AllCoreCapabilities & ~optional);

    var readOnly = Resolve(profile, MountAccessMode.ReadOnly, sourceCanWrite: false);
    var readWrite = Resolve(profile, MountAccessMode.ReadWrite, sourceCanWrite: true);

    Assert.Multiple(() => {
      Assert.That(readOnly.IsSupported, Is.True);
      Assert.That(readWrite.IsSupported, Is.True);
      Assert.That(readOnly.RequiredCapabilities & optional, Is.EqualTo(FilesystemDriverCapabilities.None));
      Assert.That(readWrite.RequiredCapabilities & optional, Is.EqualTo(FilesystemDriverCapabilities.None));
    });
  }

  [Test]
  public void BackendSpecificCapabilitiesAreReportedExactly() {
    var requiredRead = FilesystemDriverCapabilities.SetMetadata;
    var requiredWrite = FilesystemDriverCapabilities.SymbolicLinks;
    var backend = Backend(requiredReadCapabilities: requiredRead, requiredWriteCapabilities: requiredWrite);
    var plan = Resolve(Profile(), MountAccessMode.ReadWrite, sourceCanWrite: true, backend: backend);

    Assert.Multiple(() => {
      Assert.That(plan.IsSupported, Is.False);
      Assert.That(plan.MissingCapabilities, Is.EqualTo(requiredRead | requiredWrite));
      Assert.That(plan.Reasons.Where(static reason => reason.Code == MountSupportReasonCode.MissingDriverCapabilities)
        .Select(static reason => reason.MissingCapabilities),
        Is.EquivalentTo(new[] { requiredRead, requiredWrite }));
    });
  }

  [Test]
  public void RegistryRejectsDuplicateBackendIdsCaseInsensitively() {
    var first = new StubBackend(Backend(id: "dokan"));
    var duplicate = new StubBackend(Backend(id: "DOKAN"));

    Assert.That(
      () => new MountBackendRegistry(new IFilesystemMountBackend[] { first, duplicate }),
      Throws.ArgumentException
    );
  }

  private static MountPlan Resolve(
    FilesystemDriverProfile profile,
    MountAccessMode accessMode,
    bool sourceCanWrite,
    MountBackendProfile? backend = null
  ) => FilesystemMountCapabilityResolver.Resolve(
    profile,
    backend ?? Backend(),
    accessMode,
    sourceCanWrite
  );

  private static FilesystemDriverProfile Profile(
    FilesystemDriverCapabilities? capabilities = null,
    FilesystemMutationModel mutationModel = FilesystemMutationModel.Direct,
    bool canMount = true,
    bool canMountWritable = true
  ) => new(
    "testfs",
    "synthetic",
    capabilities ?? AllCoreCapabilities,
    mutationModel,
    canMount,
    canMountWritable,
    Array.Empty<string>()
  );

  private static MountBackendProfile Backend(
    string id = "test",
    bool isAvailable = true,
    bool supportsReadOnly = true,
    bool supportsReadWrite = true,
    FilesystemDriverCapabilities requiredReadCapabilities = FilesystemDriverCapabilities.None,
    FilesystemDriverCapabilities requiredWriteCapabilities = FilesystemDriverCapabilities.None
  ) => new(
    id,
    "Synthetic backend",
    isAvailable,
    supportsReadOnly,
    supportsReadWrite,
    requiredReadCapabilities,
    requiredWriteCapabilities,
    Array.Empty<string>()
  );

  private sealed class StubBackend(MountBackendProfile profile) : IFilesystemMountBackend {
    public MountBackendProfile GetProfile() => profile;

    public ValueTask<IMountSession> MountAsync(
      FilesystemMountRequest request,
      CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();
  }
}
