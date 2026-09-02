#pragma warning disable CS1591
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Represents a gzexe executable packer handler.
/// </summary>
public sealed class GzexeExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "gzexe";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "gzexe executable wrapper";
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = GzexeFormatDescriptor.LocateEmbeddedGzip(bytes) >= 0;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No gzexe shell wrapper with embedded gzip payload was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) =>
    ShellWrapperHandlerSupport.Parse(this.Id, image, detection, this.Capabilities);

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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

/// <summary>
/// Represents a bzexe executable packer handler.
/// </summary>
public sealed class BzexeExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "bzexe";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "bzexe executable wrapper";
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = BzexeFormatDescriptor.LocateEmbeddedBzip2(bytes) >= 0;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No bzexe shell wrapper with embedded BZip2 payload was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) =>
    ShellWrapperHandlerSupport.Parse(this.Id, image, detection, this.Capabilities);

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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

/// <summary>
/// Represents a papaw executable packer handler.
/// </summary>
public sealed class PapawExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "papaw";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Papaw executable wrapper";
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
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

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = PapawFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Papaw ELF wrapper with appended XZ/LZMA2 payload was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
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

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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

/// <summary>
/// Represents a go packer executable packer handler.
/// </summary>
public sealed class GoPackerExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "gopacker";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "GoPacker executable wrapper";
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
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

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = GoPackerFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No GoPacker wrapper with appended Zstandard payload was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
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

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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

/// <summary>
/// Represents an origami executable packer handler.
/// </summary>
public sealed class OrigamiExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "origami";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Origami .NET executable wrapper";
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = OrigamiFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Origami managed PE payload metadata was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
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

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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

/// <summary>
/// Represents a silent packer executable packer handler.
/// </summary>
public sealed class SilentPackerExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "silent_packer";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Silent_Packer ELF XOR wrapper";
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX64;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = SilentPackerFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Silent_Packer ELF64 XOR section-insertion metadata was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
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

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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

/// <summary>
/// Represents a huan executable packer handler.
/// </summary>
public sealed class HuanExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "huan";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Huan PE64 encrypted loader";
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX64;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    var bytes = image.ToArray();
    var match = HuanFormatDescriptor.LocatePayload(bytes) != null;
    return new(match, this.Id, match ? 1.0 : 0.0,
      match ? [] : [new(ExecutableDiagnosticCode.NotPackedExecutable, "No Huan .huan AES payload section was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
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

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
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
