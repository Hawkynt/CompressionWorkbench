using Compression.Registry;

namespace Compression.Tests.Maintenance;

/// <summary>
/// The maintenance capability model: each operation is advertised through exactly one
/// interface, nothing implies anything else, and no format advertises an operation it has
/// been shown to perform lossily.
/// </summary>
[TestFixture]
public sealed class MaintenanceCapabilitiesTests {

  [OneTimeSetUp]
  public void Register() => Compression.Lib.FormatRegistration.EnsureInitialized();

  private static MaintenanceProfile Profile(string id)
    => MaintenanceCapabilities.Describe(id) ?? throw new AssertionException($"'{id}' is not registered.");

  // ── one equivalence class per operation ────────────────────────────

  [TestCase("Zstd"), TestCase("Gzip"), TestCase("Xz"), TestCase("Bzip2"), TestCase("Lz4")]
  [Category("Registry")]
  public void GivenAPureCodecStream_ThenItCompressesAndDoesNothingElse(string id) {
    var profile = Profile(id);
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.Compress), Is.True);
      Assert.That(profile.Operations & (MaintenanceCapability.Canonicalize | MaintenanceCapability.Repack
        | MaintenanceCapability.SortDirectoryEntries | MaintenanceCapability.ChangeGeometry
        | MaintenanceCapability.DefragmentExtents), Is.EqualTo(MaintenanceCapability.None));
      Assert.That(profile.Supports(MaintenanceCapability.Compact), Is.True, "compress alone makes compact worth offering");
    });
  }

  [Test, Category("Registry")]
  public void GivenZip_ThenItCompressesAndRepacksButDoesNotDefragmentOrCanonicalize() {
    var profile = Profile("Zip");
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.Compress | MaintenanceCapability.Repack), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.Canonicalize), Is.False);
      Assert.That(profile.Supports(MaintenanceCapability.DefragmentExtents), Is.False, "an archive has no extents to move");
      Assert.That(profile.DefragFeatures, Is.EqualTo(DefragFeature.None));
    });
  }

  [Test, Category("Registry")]
  public void GivenSevenZip_ThenItCompressesButDoesNotRepack() {
    var profile = Profile("SevenZip");
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.Compress), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.Repack), Is.False,
        "a 7z rewrite always recompresses, so it is compress, not a verbatim repack");
    });
  }

  [Test, Category("Registry")]
  public void GivenMacBinary_ThenItCanonicalizesAndDoesNotClaimCompression() {
    var profile = Profile("MacBinary");
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.Canonicalize), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.Compress), Is.False, "MacBinary stores; there is nothing to re-encode");
    });
  }

  [Test, Category("Registry")]
  public void GivenAFileInternalChunkMover_ThenItCanonicalizes() {
    var movers = FormatRegistry.All
      .Where(d => FormatRegistry.GetArchiveOps(d.Id) is IFileInternalChunkMover || d is IFileInternalChunkMover)
      .Select(d => d.Id).ToArray();
    Assume.That(movers, Is.Not.Empty);
    foreach (var id in movers)
      Assert.That(Profile(id).Supports(MaintenanceCapability.Canonicalize), Is.True, id);
  }

  [TestCase("Fat"), TestCase("ExFat")]
  [Category("Registry")]
  public void GivenFatOrExFat_ThenSortingIsOfferedAlongsideButApartFromExtentMoves(string id) {
    var profile = Profile(id);
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.SortDirectoryEntries), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.DefragmentExtents), Is.True);
      Assert.That(profile.Supports(DefragFeature.ConsolidateAtStart), Is.True);
    });
  }

  [TestCase("Ext"), TestCase("Ntfs")]
  [Category("Registry")]
  public void GivenAnInPlaceFilesystemWithoutASorter_ThenSortingIsNotOffered(string id) {
    var profile = Profile(id);
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.DefragmentExtents), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.SortDirectoryEntries), Is.False);
    });
  }

  [Test, Category("Registry")]
  public void GivenADefragmenterThatOnlyRebuilds_ThenItIsNotAnExtentDefragmenter() {
    var rebuildOnly = FormatRegistry.All
      .Select(d => (d.Id, Ops: FormatRegistry.GetArchiveOps(d.Id)))
      .Where(x => x.Ops is IArchiveDefragmentable && MaintenanceCapabilities.BlockMoverTypeOf(x.Ops) is null)
      .Select(x => x.Id).ToArray();
    Assume.That(rebuildOnly, Is.Not.Empty);
    foreach (var id in rebuildOnly)
      Assert.That(Profile(id).Supports(MaintenanceCapability.DefragmentExtents), Is.False, id);
  }

  // ── structural guards ──────────────────────────────────────────────

  /// <summary>
  /// The rebuilds of these formats were shown to drop label, serial, owners, modes,
  /// attributes, links or timestamps on volumes the real tools made; a geometry change
  /// built on them would do the same.
  /// </summary>
  [TestCase("Ext"), TestCase("Fat"), TestCase("ExFat"), TestCase("Ntfs"), TestCase("Iso"),
   TestCase("HfsPlus"), TestCase("Xfs"), TestCase("Btrfs"), TestCase("SquashFs")]
  [Category("Registry")]
  public void GivenAFormatWhoseRelayoutIsKnownToLoseMetadata_ThenGeometryIsNotOffered(string id) {
    var profile = Profile(id);
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.ChangeGeometry), Is.False);
      Assert.That(profile.GeometryOptions, Is.Empty);
    });
  }

  [Test, Category("Registry")]
  public void EveryGeometryClaimant_OptsIntoALosslessRelayout_AndOffersOnlyTaggedKeys() {
    foreach (var descriptor in FormatRegistry.All) {
      var profile = MaintenanceCapabilities.Describe(descriptor);
      if (!profile.Supports(MaintenanceCapability.ChangeGeometry)) continue;
      var layout = (ILayoutOptimizable)(FormatRegistry.GetArchiveOps(descriptor.Id) ?? (object)descriptor);
      Assert.Multiple(() => {
        Assert.That(layout.RelayoutPreservesEverything, Is.True, descriptor.Id);
        Assert.That(profile.GeometryOptions.All(static o => o.IsAllocationGeometry), Is.True, descriptor.Id);
      });
    }
  }

  [Test, Category("Registry")]
  public void TheSquashFsBlockSize_IsACompressionParameterNotGeometry() {
    var schema = (IFormatOptionsSchema)FormatRegistry.GetArchiveOps("SquashFs")!;
    Assert.That(schema.OptionsSchema.Single(static o => o.Key == "BlockSize").IsAllocationGeometry, Is.False);
  }

  /// <summary>
  /// Stream formats whose header carries per-file metadata (a stored name, a time, a mode)
  /// that their encoder would not reproduce. They must not claim the generic re-encode
  /// until they carry that header across, as gzip does.
  /// </summary>
  [TestCase("Lzop"), TestCase("Squeeze"), TestCase("Crunch"), TestCase("Kwaj"), TestCase("Szdd"), TestCase("SzCompress")]
  [Category("Registry")]
  public void GivenAStreamWithFileMetadataInItsHeader_ThenTheGenericReencodeIsNotClaimed(string id) {
    if (MaintenanceCapabilities.Describe(id) is not { } profile) Assert.Ignore($"{id} is not registered in this build.");
    else Assert.That(profile.Supports(MaintenanceCapability.Compress), Is.False);
  }

  [Test, Category("Registry")]
  public void EveryCompressionClaimant_EitherIsAStreamOrBringsItsOwnReencode() {
    foreach (var descriptor in FormatRegistry.All) {
      foreach (var target in new object?[] { descriptor, FormatRegistry.GetArchiveOps(descriptor.Id) }) {
        if (target is not ICompressionOptimizable) continue;
        var map = target.GetType().GetInterfaceMap(typeof(ICompressionOptimizable));
        var own = map.TargetMethods.Any(m => m.Name.EndsWith(nameof(ICompressionOptimizable.OptimizeCompression), StringComparison.Ordinal)
                                             && m.DeclaringType != typeof(ICompressionOptimizable));
        Assert.That(own || target is IStreamFormatOperations, Is.True,
          $"{descriptor.Id}: a container relying on the stream-only default re-encode would refuse every call");
      }
    }
  }

  [Test, Category("Registry")]
  public void EveryDescriptorWithASiblingBlockMover_NamesItExplicitly() {
    var missing = new List<string>();
    foreach (var descriptor in FormatRegistry.All) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops is not IArchiveDefragmentable || ops is IFilesystemBlockMover) continue;
      var family = ops.GetType().Name;
      foreach (var tail in new[] { "FormatDescriptor", "Descriptor" })
        if (family.EndsWith(tail, StringComparison.Ordinal)) { family = family[..^tail.Length]; break; }
      var sibling = ops.GetType().Assembly.GetType($"{ops.GetType().Namespace}.{family}BlockMover");
      if (sibling is not null && MaintenanceCapabilities.BlockMoverTypeOf(ops) != sibling)
        missing.Add($"{descriptor.Id} drives {sibling.Name} without [FilesystemBlockMover(typeof({sibling.Name}))]");
    }
    Assert.That(missing, Is.Empty, string.Join("\n", missing));
  }

  [Test, Category("Registry")]
  public void EveryExtentDefragmenter_HonoursAtLeastConsolidateAtStart() {
    foreach (var descriptor in FormatRegistry.All) {
      var profile = MaintenanceCapabilities.Describe(descriptor);
      if (profile.Supports(MaintenanceCapability.DefragmentExtents))
        Assert.That(profile.Supports(DefragFeature.ConsolidateAtStart), Is.True, descriptor.Id);
      else
        Assert.That(profile.DefragFeatures, Is.EqualTo(DefragFeature.None), descriptor.Id);
    }
  }

  [Test, Category("Registry")]
  public void TheCompactComposite_IsOfferedExactlyWhereOneOfItsStagesIs() {
    const MaintenanceCapability stages = MaintenanceCapability.DefragmentExtents | MaintenanceCapability.Compress | MaintenanceCapability.Shrink;
    foreach (var descriptor in FormatRegistry.All) {
      var operations = MaintenanceCapabilities.Describe(descriptor).Operations;
      Assert.That(operations.HasFlag(MaintenanceCapability.Compact), Is.EqualTo((operations & stages) != 0), descriptor.Id);
    }
  }

  // ── the profile record ─────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void GivenNone_ThenNoProfileClaimsToSupportIt() {
    var everything = new MaintenanceProfile("x", (MaintenanceCapability)~0, (DefragFeature)~0, []);
    Assert.Multiple(() => {
      Assert.That(everything.Supports(MaintenanceCapability.None), Is.False);
      Assert.That(everything.Supports(DefragFeature.None), Is.False);
    });
  }

  [Test, Category("Boundary")]
  public void GivenSeveralFlags_ThenAllOfThemMustBeOffered() {
    var profile = new MaintenanceProfile("x", MaintenanceCapability.Compress, DefragFeature.ConsolidateAtStart, []);
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.Compress), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.Compress | MaintenanceCapability.Repack), Is.False);
      Assert.That(profile.Supports(DefragFeature.Packing), Is.False);
    });
  }

  [Test, Category("Exception")]
  public void GivenAnUnknownId_ThenThereIsNoProfile()
    => Assert.That(MaintenanceCapabilities.Describe("NoSuchFormat"), Is.Null);

  [Test, Category("Exception")]
  public void GivenNull_ThenNothingIsOffered()
    => Assert.That(MaintenanceCapabilities.Of(null), Is.EqualTo(MaintenanceCapability.None));

  [Test, Category("Exception")]
  public void GivenAnAttributeNamingANonMover_ThenTheClaimIsRejectedLoudly()
    => Assert.That(() => MaintenanceCapabilities.BlockMoverTypeOf(new BogusMoverClaim()), Throws.InvalidOperationException);

  [FilesystemBlockMover(typeof(string))]
  private sealed class BogusMoverClaim;
}
