#pragma warning disable CS1591
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

public sealed class GzexeExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "gzexe";
  public string DisplayName => "gzexe executable wrapper";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = GzexeFormatDescriptor.LocateEmbeddedGzip(bytes) >= 0;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No gzexe shell wrapper with embedded gzip payload was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) =>
    ShellWrapperHandlerSupport.Parse(this.Id, image, detection, this.Capabilities);

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var artifacts = GzexeFormatDescriptor.BuildArtifacts(packed.OriginalImage)
        .Select(a => new UnpackArtifact(a.Name, a.Data, a.Method))
        .ToList();
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, artifacts, []);
    } catch (InvalidDataException ex) {
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, ex.Message, true),
      ]);
    }
  }
}

public sealed class BzexeExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "bzexe";
  public string DisplayName => "bzexe executable wrapper";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = BzexeFormatDescriptor.LocateEmbeddedBzip2(bytes) >= 0;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No bzexe shell wrapper with embedded BZip2 payload was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) =>
    ShellWrapperHandlerSupport.Parse(this.Id, image, detection, this.Capabilities);

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var artifacts = BzexeFormatDescriptor.BuildArtifacts(packed.OriginalImage)
        .Select(a => new UnpackArtifact(a.Name, a.Data, a.Method))
        .ToList();
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, artifacts, []);
    } catch (InvalidDataException ex) {
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, ex.Message, true),
      ]);
    }
  }
}

public sealed class PapawExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "papaw";
  public string DisplayName => "Papaw executable wrapper";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64 |
    ExecutableUnpackCapabilities.SupportsArm32 |
    ExecutableUnpackCapabilities.SupportsArm64;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = PapawFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Papaw ELF wrapper with appended XZ/LZMA2 payload was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var imageInfo = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      imageInfo,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.Id,
        ["container"] = imageInfo.Container.ToString(),
        ["architecture"] = imageInfo.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var artifacts = PapawFormatDescriptor.BuildArtifacts(packed.OriginalImage)
        .Select(a => new UnpackArtifact(a.Name, a.Data, a.Method))
        .ToList();
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, artifacts, []);
    } catch (InvalidDataException ex) {
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, ex.Message, true),
      ]);
    }
  }
}

public sealed class GoPackerExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "gopacker";
  public string DisplayName => "GoPacker executable wrapper";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64 |
    ExecutableUnpackCapabilities.SupportsArm32 |
    ExecutableUnpackCapabilities.SupportsArm64;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = GoPackerFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No GoPacker wrapper with appended Zstandard payload was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var imageInfo = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      imageInfo,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.Id,
        ["container"] = imageInfo.Container.ToString(),
        ["architecture"] = imageInfo.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var artifacts = GoPackerFormatDescriptor.BuildArtifacts(packed.OriginalImage)
        .Select(a => new UnpackArtifact(a.Name, a.Data, a.Method))
        .ToList();
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, artifacts, []);
    } catch (InvalidDataException ex) {
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, ex.Message, true),
      ]);
    }
  }
}

public sealed class OrigamiExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "origami";
  public string DisplayName => "Origami .NET executable wrapper";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = OrigamiFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Origami managed PE payload metadata was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var imageInfo = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      imageInfo,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.Id,
        ["container"] = imageInfo.Container.ToString(),
        ["architecture"] = imageInfo.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var artifacts = OrigamiFormatDescriptor.BuildArtifacts(packed.OriginalImage)
        .Select(a => new UnpackArtifact(a.Name, a.Data, a.Method))
        .ToList();
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, artifacts, []);
    } catch (InvalidDataException ex) {
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, ex.Message, true),
      ]);
    }
  }
}

public sealed class SilentPackerExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "silent_packer";
  public string DisplayName => "Silent_Packer ELF XOR wrapper";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX64;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = SilentPackerFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Silent_Packer ELF64 XOR section-insertion metadata was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var imageInfo = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      imageInfo,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.Id,
        ["container"] = imageInfo.Container.ToString(),
        ["architecture"] = imageInfo.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var artifacts = SilentPackerFormatDescriptor.BuildArtifacts(packed.OriginalImage)
        .Select(a => new UnpackArtifact(a.Name, a.Data, a.Method))
        .ToList();
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, artifacts, []);
    } catch (InvalidDataException ex) {
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, ex.Message, true),
      ]);
    }
  }
}

public sealed class HuanExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "huan";
  public string DisplayName => "Huan PE64 encrypted loader";
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX64;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = HuanFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Huan .huan AES payload section was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var imageInfo = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      imageInfo,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.Id,
        ["container"] = imageInfo.Container.ToString(),
        ["architecture"] = imageInfo.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    try {
      var artifacts = HuanFormatDescriptor.BuildArtifacts(packed.OriginalImage)
        .Select(a => new UnpackArtifact(a.Name, a.Data, a.Method))
        .ToList();
      return new(ExecutableUnpackLevel.RebuiltExecutable, this.Capabilities, artifacts, []);
    } catch (InvalidDataException ex) {
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [], [
        new(ExecutableDiagnosticCode.PayloadNotFound, ex.Message, true),
      ]);
    }
  }
}

internal static class ShellWrapperHandlerSupport {
  public static PackedExecutable Parse(
    string packerId,
    ReadOnlySpan<byte> image,
    DetectionResult detection,
    ExecutableUnpackCapabilities capabilities
  ) {
    var imageBytes = image.ToArray();
    var info = new ExecutableImageInfo(
      ExecutableContainerKind.Unknown,
      CpuArchitecture.Unknown,
      0,
      0,
      [],
      [],
      [],
      [new(ExecutableDiagnosticCode.UnsupportedContainer,
        "Shell-wrapper executable packers replace the original executable container with a POSIX shell script wrapper.")]
    );

    return new(
      packerId,
      imageBytes,
      detection,
      info,
      capabilities,
      new Dictionary<string, string> {
        ["packer"] = packerId,
        ["container"] = "shell-script-wrapper",
        ["architecture"] = "unknown",
      });
  }
}
