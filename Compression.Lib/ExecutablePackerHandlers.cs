using Compression.Core.ExecutableUnpacking;
using FileFormat.ExePackers;
using FileFormat.Upx;

namespace Compression.Lib;

public sealed record ExecutablePackerMatch(IExecutablePackerHandler Handler, DetectionResult Detection);

public static class ExecutablePackerHandlers {
  private static readonly Lazy<IReadOnlyList<IExecutablePackerHandler>> RegisteredHandlers = new(() => [
    new UpxExecutablePackerHandler(),
    new FsgExecutablePackerHandler(),
    new AsPackExecutablePackerHandler(),
    new PeCompactExecutablePackerHandler(),
    new RlPackExecutablePackerHandler(),
    new PackmanExecutablePackerHandler(),
    new EnigmaVirtualBoxExecutablePackerHandler(),
    new PeToyExecutablePackerHandler(),
    new GzexeExecutablePackerHandler(),
    new BzexeExecutablePackerHandler(),
    new PapawExecutablePackerHandler(),
    new GoPackerExecutablePackerHandler(),
    new OrigamiExecutablePackerHandler(),
    new PyPePackerExecutablePackerHandler(),
    new SilentPackerExecutablePackerHandler(),
    new HuanExecutablePackerHandler(),
    new XorPackerExecutablePackerHandler(),
    new HxorPackerExecutablePackerHandler(),
    new SimpleDpackExecutablePackerHandler(),
    new YodaCrypterExecutablePackerHandler(),
    new MewExecutablePackerHandler(),
    new PetiteExecutablePackerHandler(),
    new MPressExecutablePackerHandler(),
    new NsPackExecutablePackerHandler(),
    new GenericAplibPackedPeHandler(),
    new WinUpackExecutablePackerHandler(),
    new GenericNrvPackedPeHandler(),

    // ELF crypters/packers (packing-box campaign)
    new EzuriExecutablePackerHandler(),
    new WardExecutablePackerHandler(),
    new M0dernP4ckerExecutablePackerHandler(),
    new MidgetPackExecutablePackerHandler(),

    // Descriptor-wrapped handlers
    new DescriptorExecutablePackerHandler(new AsProtectFormatDescriptor()),
    new DescriptorExecutablePackerHandler(new CrinklerFormatDescriptor()),
    new DescriptorExecutablePackerHandler(new KkrunchyFormatDescriptor()),
    new DescriptorExecutablePackerHandler(new LzExeFormatDescriptor()),
    new DescriptorExecutablePackerHandler(new PkLiteFormatDescriptor()),
    new DescriptorExecutablePackerHandler(new ShrinklerFormatDescriptor()),
    new DescriptorExecutablePackerHandler(new VmProtectFormatDescriptor()),

    // Planned custom handlers
    new AlienyzeExecutablePackerHandler(),
    new AmberExecutablePackerHandler(),
    new BeRoExecutablePackerHandler(),
    new EronanaExecutablePackerHandler(),
    new Exe32packExecutablePackerHandler(),
    new ExpressorExecutablePackerHandler(),
    new JdpackExecutablePackerHandler(),
    new MoleboxExecutablePackerHandler(),
    new NeoliteExecutablePackerHandler(),
    new YodaProtectorExecutablePackerHandler(),
    new ThemidaExecutablePackerHandler(),
    new TelockExecutablePackerHandler(),
    new WinUpackFallbackExecutablePackerHandler(),
    new FsgFallbackExecutablePackerHandler(),
  ]);

  public static IReadOnlyList<IExecutablePackerHandler> All => RegisteredHandlers.Value;

  public static ExecutablePackerMatch? DetectBest(ReadOnlySpan<byte> image) {
    ExecutablePackerMatch? best = null;
    foreach (var handler in All) {
      var detection = handler.Detect(image);
      if (!detection.IsMatch)
        continue;
      if (best == null || detection.Confidence > best.Detection.Confidence)
        best = new(handler, detection);
    }
    return best;
  }

  public static UnpackResult? TryUnpack(ReadOnlySpan<byte> image, UnpackOptions? options = null) {
    var match = DetectBest(image);
    if (match == null)
      return null;
    var packed = match.Handler.Parse(image, match.Detection);
    return match.Handler.Unpack(packed, options ?? new());
  }
}
