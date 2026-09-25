#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Documentation;

namespace Compression.Tests.Operations;

/// <summary>
/// Every verb the filesystem support matrix ticks is reachable in the code that
/// claims it.
/// </summary>
/// <remarks>
/// <para>Most of the verb columns are rendered from marker interfaces —
/// <c>ops is IArchiveDefragmentable</c> and its siblings. An <c>is</c>-check
/// cannot tell an implemented verb from a declared one whose only statement is
/// <c>throw new NotSupportedException</c>, so a descriptor could carry the marker
/// for documentation and still render a green check it does not honour. That is
/// what this fixture closes.</para>
///
/// <para>The discriminator is the exception type. A verb that is <em>implemented</em>
/// fails on the DATA it was handed — bad magic, a truncated image, an unreadable
/// superblock. A verb that is <em>absent</em> fails on the CAPABILITY, and the
/// contract spells that <see cref="NotSupportedException"/>. So the probe runs the
/// verb against the format's own freshly written image where it can write one, and
/// against an empty stream where it cannot, and only a capability refusal fails the
/// test. Every other exception is the format telling the probe about the bytes,
/// which is not what is being asked.</para>
/// </remarks>
[TestFixture]
public sealed class FilesystemVerbsAreBackedTests {

  /// <summary>
  /// Verbs whose removal cannot land in the pass that found them, and why. Each
  /// entry names a descriptor that declares a marker whose only possible answer
  /// is a capability refusal, so the support matrix would tick a cell the code
  /// cannot honour. An entry earns its place only while the cell is still ticked;
  /// the honest fix is to stop ticking it.
  /// </summary>
  private static readonly Dictionary<string, string> Deferred = new(StringComparer.Ordinal);

  private static IEnumerable<TestCaseData> DefragmentableIds() {
    foreach (var descriptor in Descriptors())
      if (OptimizationCapabilities.CanDefragmentExtents(descriptor))
        yield return new TestCaseData(descriptor.Id).SetName($"DefragmentExtentsIsBacked_{descriptor.Id}");
  }
  private static IEnumerable<TestCaseData> WipeableIds() => Ids(typeof(IWipeEmpty), "Wipe");
  private static IEnumerable<TestCaseData> ShrinkableIds() => Ids(typeof(IArchiveShrinkable), "Shrink");

  /// <summary>
  /// The layout probe follows the rendered cell rather than the marker, because
  /// the two are no longer the same question. A descriptor declares
  /// <see cref="ILayoutOptimizable"/> to publish its geometry analysis, which is
  /// real work; only the ones the matrix ticks claim a rebuild.
  /// </summary>
  private static IEnumerable<TestCaseData> LayoutIds() {
    foreach (var descriptor in Descriptors())
      if (OptimizationCapabilities.CanChangeAllocationGeometry(descriptor))
        yield return new TestCaseData(descriptor.Id).SetName($"GeometryChangeIsBacked_{descriptor.Id}");
  }
  private static IEnumerable<TestCaseData> BlockMoverIds() {
    foreach (var descriptor in Descriptors())
      if (OptimizationCapabilities.HasFilesystemBlockMover(descriptor))
        yield return new TestCaseData(descriptor.Id).SetName($"BlockMoverIsExplicit_{descriptor.Id}");
  }

  private static IEnumerable<TestCaseData> Ids(Type marker, string verb) {
    foreach (var descriptor in Descriptors()) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops != null && marker.IsAssignableFrom(ops.GetType()))
        yield return new TestCaseData(descriptor.Id).SetName($"{verb}IsBacked_{descriptor.Id}");
    }
  }

  private static IReadOnlyList<IFormatDescriptor> Descriptors()
    => FilesystemSupportMatrix.Descriptors(FilesystemReadmeIsCurrentTests.RepositoryRoot());

  [TestCaseSource(nameof(DefragmentableIds))]
  public void ADeclaredExtentDefrag_IsNotACapabilityRefusal(string formatId)
    => Probe(formatId, "Defragment extents", (ops, image) => ((IArchiveDefragmentable)ops).Defragment(image));

  [TestCaseSource(nameof(WipeableIds))]
  public void ADeclaredWipe_IsNotACapabilityRefusal(string formatId)
    => Probe(formatId, "Wipe", (ops, image) => ((IWipeEmpty)ops).WipeUnusedSpace(image));

  [TestCaseSource(nameof(ShrinkableIds))]
  public void ADeclaredShrink_IsNotACapabilityRefusal(string formatId)
    => Probe(formatId, "Shrink", (ops, image) => {
      using var target = new MemoryStream();
      ((IArchiveShrinkable)ops).Shrink(image, target);
    });

  [TestCaseSource(nameof(LayoutIds))]
  public void ADeclaredGeometryChange_IsNotACapabilityRefusal(string formatId)
    => Probe(formatId, "Change allocation geometry", (ops, image) => {
      using var target = new MemoryStream();
      ((ILayoutOptimizable)ops).RebuildStreaming(image, target, new LayoutRebuildOptions());
    });

  /// <summary>A deferral that has been dealt with elsewhere has to leave the list.</summary>
  [Test]
  public void Deferred_ClaimsAreStillDeclared() {
    var gone = new List<string>();
    foreach (var key in Deferred.Keys) {
      var separator = key.IndexOf(':', StringComparison.Ordinal);
      var id = key[..separator];
      var verb = key[(separator + 1)..];
      var descriptor = FormatRegistry.GetById(id);
      var ops = FormatRegistry.GetArchiveOps(id);
      var stillDeclared = verb switch {
        "Defragment extents" => OptimizationCapabilities.CanDefragmentExtents(descriptor),
        "Change allocation geometry" => OptimizationCapabilities.CanChangeAllocationGeometry(descriptor),
        "Wipe" => ops is IWipeEmpty,
        "Shrink" => ops is IArchiveShrinkable,
        _ => false,
      };
      if (!stillDeclared) gone.Add(key);
    }
    Assert.That(gone, Is.Empty, "These no longer declare the verb, so their deferral is stale — remove it: " + string.Join(", ", gone));
  }

  [TestCaseSource(nameof(BlockMoverIds))]
  public void AResolvedBlockMover_IsConcreteAndUnambiguous(string formatId) {
    var descriptor = Descriptors().Single(candidate => candidate.Id == formatId);
    var moverType = OptimizationCapabilities.GetFilesystemBlockMoverType(descriptor);

    Assert.Multiple(() => {
      Assert.That(moverType, Is.Not.Null);
      Assert.That(typeof(IFilesystemBlockMover).IsAssignableFrom(moverType!), Is.True);
      Assert.That(moverType!.IsClass, Is.True);
      Assert.That(moverType.IsAbstract, Is.False);
    });
  }


  /// <summary>
  /// Runs <paramref name="verb"/> against an image the format wrote itself where
  /// it can write one, and fails only on a capability refusal.
  /// </summary>
  private static void Probe(string formatId, string column, Action<object, MemoryStream> run) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    using var image = new MemoryStream();
    if (ops is IArchiveCreatable creator) {
      try {
        creator.Create(image, [ArchiveInputInfo.InMemory("PROBE.BIN", new byte[512])], new FormatCreateOptions());
      } catch {
        image.SetLength(0);
      }
      image.Position = 0;
    }

    try {
      run(ops, image);
    } catch (NotSupportedException ex) when (Deferred.TryGetValue(formatId + ":" + column, out var reason)) {
      Assert.Ignore($"{formatId} {column} is a known unbacked claim, deferred: {reason} ({ex.GetType().Name})");
    } catch (NotSupportedException ex) {
      Assert.Fail(
        $"{formatId} is rendered with a {column} tick because its ops declares the marker interface, "
        + $"but the verb refuses as a capability: {ex.Message}\n"
        + "Either implement it or drop the interface, so the support matrix stops claiming it.");
    } catch (Exception) {
      // A data-level failure is the format reading the probe image, not refusing
      // the verb. The claim under test is that the verb EXISTS.
    }
  }
}
