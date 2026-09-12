#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Represents a minor executable packer handler base.
/// </summary>
public abstract class MinorExecutablePackerHandlerBase : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public abstract string Id { get; }
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public abstract string DisplayName { get; }

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public virtual ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected abstract bool IsPackerSection(string name);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected abstract ReadOnlySpan<byte> LiteralSignature { get; }

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public virtual DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s => IsPackerSection(s.Name));
    var hasLiteral = LiteralSignature.Length > 0 && image.IndexOf(LiteralSignature) >= 0;

    var match = hasSection || hasLiteral;
    return match
      ? new(true, this.Id, 0.85, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{this.DisplayName} signature not found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      info,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = this.DisplayName,
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public virtual UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect;
    var rebuilt = false;

    // 1. Locate packer sections
    var sections = PackerScanner.GetPeSectionRanges(packed.OriginalImage);
    var locatedPayloads = new List<(string Name, byte[] Data)>();
    foreach (var s in sections) {
      if (IsPackerSection(s.Name) && s.RawSize > 0) {
        var len = (int)Math.Min(s.RawSize, (uint)(packed.OriginalImage.Length - s.RawOffset));
        var data = packed.OriginalImage.AsSpan((int)s.RawOffset, len).ToArray();
        locatedPayloads.Add((s.Name, data));
        level = ExecutableUnpackLevel.PayloadLocated;
        caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      }
    }
    AddLocatedPayloadArtifacts(artifacts, locatedPayloads);

    // 2. Try generic aPLib decompress
    if (AplibSectionPackerHandler.TryFindAplibPayload(packed.OriginalImage, options.MaximumDecompressedSize, out var decoded)) {
      artifacts.Add(new("decompressed_payload.bin", decoded, "stored"));
      level = ExecutableUnpackLevel.PayloadDecompressed;
      caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
      if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info) {
        try {
          var pe = PeRebuilder.RebuildSynthetic(info, decoded);
          artifacts.Add(new("reconstructed/reconstructed.exe", pe, "stored"));
          level = ExecutableUnpackLevel.RebuiltExecutable;
          caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
          rebuilt = true;
        } catch {
          // Ignored
        }
      }
    }
    // 3. Try generic NRV decompress if not rebuilt yet
    else if (!rebuilt && GenericNrvPackedPeHandler.TryFindNrvPayload(packed.OriginalImage, options.MaximumDecompressedSize, out var nrvDecoded)) {
      artifacts.Add(new("decompressed_payload.bin", nrvDecoded, "stored"));
      level = ExecutableUnpackLevel.PayloadDecompressed;
      caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
      if (packed.ImageInfo is { Container: ExecutableContainerKind.Pe } info) {
        try {
          var pe = PeRebuilder.RebuildSynthetic(info, nrvDecoded);
          artifacts.Add(new("reconstructed/reconstructed.exe", pe, "stored"));
          level = ExecutableUnpackLevel.RebuiltExecutable;
          caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
        } catch {
          // Ignored
        }
      }
    }

    if (level == ExecutableUnpackLevel.DetectionOnly) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, $"{this.DisplayName} detected but no packed payload section found.", true));
    } else if (level == ExecutableUnpackLevel.PayloadLocated) {
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"{this.DisplayName} payload located, but it could not be decoded as a cleanly-terminated aPLib or NRV stream.", true));
    }

    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64) caps |= ExecutableUnpackCapabilities.SupportsX64;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>
  /// Performs the build metadata json operation.
  /// </summary>
  protected byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{this.Id}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void AddLocatedPayloadArtifacts(List<UnpackArtifact> artifacts, IReadOnlyList<(string Name, byte[] Data)> payloads) {
    if (payloads.Count == 0)
      return;
    if (payloads.Count == 1) {
      artifacts.Add(new("compressed_payload.bin", payloads[0].Data, "stored"));
      return;
    }

    for (var i = 0; i < payloads.Count; i++)
      artifacts.Add(new($"payload_candidates/candidate_{i:000}_{Sanitize(payloads[i].Name)}.bin", payloads[i].Data, "stored"));
  }

  private static string Sanitize(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "payload" : sb.ToString();
  }
}

/// <summary>
/// Base for runtime protectors (TELock, Yoda's Protector, the Themida fallback,
/// …) whose original image is only recoverable by executing the anti-debug /
/// code-virtualization stub under emulation. These handlers honestly stay at
/// detection + payload-location and deliberately do <b>not</b> run the generic
/// aPLib/NRV probes: a spuriously "cleanly terminated" stream inside a
/// protector's encrypted body would fabricate a decompression we cannot
/// actually perform. Every result carries an explicit runtime-protector
/// diagnostic so callers never mistake location for unpacking.
/// </summary>
public abstract class ProtectorExecutablePackerHandlerBase : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public override ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect;

    foreach (var s in PackerScanner.GetPeSectionRanges(packed.OriginalImage)) {
      if (s.RawSize == 0 || s.RawOffset >= packed.OriginalImage.Length)
        continue;
      if (!this.IsPackerSection(s.Name) && !LooksProtected(s))
        continue;
      var len = (int)Math.Min(s.RawSize, (uint)(packed.OriginalImage.Length - s.RawOffset));
      artifacts.Add(new($"protected_section_{Sanitize(s.Name)}.bin",
        packed.OriginalImage.AsSpan((int)s.RawOffset, len).ToArray(), "stored"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    }

    diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod, this.StaticUnpackObstacle, true));

    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>
  /// Why this handler stops at payload location. The default is the honest
  /// answer for a virtualizing protector; handlers whose obstacle is something
  /// more specific should say so rather than let a user read "needs emulation"
  /// where that is not the reason.
  /// </summary>
  protected virtual string StaticUnpackObstacle =>
    $"{this.DisplayName}: static full unpack not feasible (runtime protector). The original " +
    "image is reconstructed only by executing the anti-debug / code-virtualization stub, so the " +
    "handler stops at payload location; no managed decompression is attempted.";

  /// <summary>An RWX section of a protector still counts as the located protected body.</summary>
  private static bool LooksProtected(PackerScanner.PeSectionRange s) {
    const uint rwx = 0x20000000u | 0x40000000u | 0x80000000u;
    return (s.Characteristics & rwx) == rwx;
  }

  private static string Sanitize(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "section" : sb.ToString();
  }
}

/// <summary>
/// Represents an alienyze executable packer handler.
/// </summary>
public sealed class AlienyzeExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "alienyze";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Alienyze";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) => name.Contains("alien", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "Alienyze"u8;
}

/// <summary>
/// Represents an amber executable packer handler.
/// </summary>
public sealed class AmberExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "amber";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Amber reflective PE loader";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) => false;
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "amber"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var hasAscii = image.IndexOf("amber"u8) >= 0 || image.IndexOf("Amber"u8) >= 0 || image.IndexOf("AMBER"u8) >= 0;
    var hasUtf16 = image.IndexOf("A\0m\0b\0e\0r\0"u8) >= 0 || image.IndexOf("a\0m\0b\0e\0r\0"u8) >= 0;
    if (hasAscii || hasUtf16)
      return new(true, this.Id, 0.90, []);
    return new(false, this.Id, 0, []);
  }

  /// <summary>
  /// Amber turns a PE into position-independent shellcode plus an embedded copy
  /// of the original image that its reflective loader maps at runtime. When that
  /// embedded copy is stored as a plaintext <c>MZ..PE</c> (the loader relocates it
  /// in place) we carve and validate it as a real PE. When it is XOR/RC4-obscured
  /// — the common case — the key lives in the shellcode stub, so we honestly stop
  /// at locating the payload-bearing region rather than fabricating a decode.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildAmberMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var embedded = FindEmbeddedPe(image);
    if (embedded is { } pe) {
      artifacts.Add(new("embedded_pe.bin", pe, "stored"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        "Amber: a plaintext embedded PE was carved from the reflective payload; " +
        "runtime relocation/import fix-ups applied by the loader are not replayed."));
    } else {
      var region = LargestPayloadRegion(image);
      if (region is { } r) {
        artifacts.Add(new("reflective_payload.bin", image.AsSpan(r.Offset, r.Length).ToArray(), "amber-section"));
        level = ExecutableUnpackLevel.PayloadLocated;
        caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      }
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "Amber: reflective payload located but the embedded PE is obfuscated (XOR/RC4 keyed by the " +
        "shellcode stub); static decryption is not feasible without executing the loader.", true));
    }

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[]? FindEmbeddedPe(byte[] b) {
    // Skip the outer image's own PE header; look for a second MZ..PE further in.
    for (var i = 0x200; i + 0x40 < b.Length; i++) {
      if (b[i] != 'M' || b[i + 1] != 'Z') continue;
      var e = BitConverter.ToInt32(b, i + 0x3C);
      if (e is <= 0 or > 0x1000 || i + e + 4 >= b.Length) continue;
      if (b[i + e] == 'P' && b[i + e + 1] == 'E' && b[i + e + 2] == 0 && b[i + e + 3] == 0)
        return b.AsSpan(i, b.Length - i).ToArray();
    }
    return null;
  }

  private static (int Offset, int Length)? LargestPayloadRegion(byte[] image) {
    (int Offset, int Length)? best = null;
    foreach (var s in PackerScanner.GetPeSectionRanges(image)) {
      if (s.RawSize < 256 || s.RawOffset >= image.Length) continue;
      var len = (int)Math.Min(s.RawSize, (uint)(image.Length - s.RawOffset));
      if (best is null || len > best.Value.Length)
        best = ((int)s.RawOffset, len);
    }
    return best;
  }

  private byte[] BuildAmberMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"amber\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"loaderType\": \"reflective-pe-loader\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

/// <summary>
/// Represents an exe 32pack executable packer handler.
/// </summary>
public sealed class Exe32packExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "exe32pack";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Exe32pack";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) =>
    name is ".i" or ".f" or ".c" or ".v" or ".h";
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "exe32pack"u8;
  
  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasExe32 = image.IndexOf("exe32pack"u8) >= 0;
    var hasKnownSection = sections.Any(s => s.Name == ".i" || s.Name == ".f" || s.Name == ".c" || s.Name == ".v" || s.Name == ".h");
    if (hasExe32 || hasKnownSection) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

/// <summary>
/// Represents a neolite executable packer handler.
/// </summary>
public sealed class NeoliteExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "neolite";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Neolite";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) => name.Contains("neolit", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "NeoLite"u8;
}

/// <summary>
/// Represents a ns pack executable packer handler.
/// </summary>
public sealed class NsPackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "nspack";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "NSPack";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) =>
    name.StartsWith("nsp", StringComparison.OrdinalIgnoreCase) ||
    name.StartsWith(".nsp", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "NsPack"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s => IsPackerSection(s.Name));
    var hasLiteral = image.IndexOf(LiteralSignature) >= 0;
    return hasSection || hasLiteral
      ? new(true, this.Id, hasSection ? 0.93 : 0.88, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "NSPack marker was not found.", true)]);
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    var payload = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => IsPackerSection(s.Name) && s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.Name.Equals("nsp1", StringComparison.OrdinalIgnoreCase) || s.Name.Equals(".nsp1", StringComparison.OrdinalIgnoreCase))
      .ThenByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (payload.RawSize == 0)
      return generic;

    if (generic.Level >= ExecutableUnpackLevel.PayloadDecompressed && generic.Artifacts.Any(a => a.Name == "compressed_payload.bin"))
      return generic;

    var artifacts = generic.Artifacts
      .Where(a => a.Name != "diagnostics.json" && a.Name != "compressed_payload.bin" && !a.Name.StartsWith("payload_candidates/", StringComparison.Ordinal))
      .ToList();
    var len = (int)Math.Min(payload.RawSize, (uint)(packed.OriginalImage.Length - payload.RawOffset));
    artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)payload.RawOffset, len).ToArray(), "nspack-section"));

    if (generic.Level >= ExecutableUnpackLevel.PayloadDecompressed) {
      var passthrough = generic with { Artifacts = artifacts };
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, passthrough), "stored"));
      return passthrough with { Artifacts = artifacts };
    }

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"NSPack payload section '{payload.Name}' was located. Managed NSPack decompression and transform reversal are not implemented.",
        true),
    };
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}

/// <summary>
/// Represents a petite executable packer handler.
/// </summary>
public sealed class PetiteExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "petite";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "PEtite";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) => name.StartsWith(".petite", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "Petite"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasPetiteSection = sections.Any(s => IsPackerSection(s.Name));
    var hasMarker = image.IndexOf("Petite"u8) >= 0;
    return hasPetiteSection || hasMarker
      ? new(true, this.Id, hasPetiteSection ? 0.93 : 0.82, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "PEtite marker was not found.", true)]);
  }

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public override ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>
  /// Expands the PEtite block table (see <see cref="PetiteUnpacker"/>). Each
  /// original section comes back as its own artifact; imports, relocations and
  /// the original entry point are not restored, so the result stays at
  /// <see cref="ExecutableUnpackLevel.PayloadDecompressed"/>.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    if (!PetiteUnpacker.TryUnpack(packed.OriginalImage, options.MaximumDecompressedSize, out var unpacked, out var failure) || unpacked == null)
      return AddFailureDiagnostic(base.Unpack(packed, options), failure);

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
    };
    var code = unpacked.Blocks.FirstOrDefault(b => b.BranchFilterReversed);
    var payload = code.Data ?? unpacked.Blocks[0].Data;
    artifacts.Add(new("decompressed_payload.bin", payload, "petite-deflate"));
    foreach (var block in unpacked.Blocks)
      artifacts.Add(new(
        $"sections/rva_{block.DestinationRva:X8}.bin",
        block.Data,
        block.BranchFilterReversed ? "petite-deflate+branch" : "petite-deflate"));
    artifacts.Add(new("memory_image.bin", unpacked.MemoryImage, "petite-deflate"));

    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        $"PEtite: {unpacked.Blocks.Count} block(s) expanded from the table at RVA 0x{unpacked.BlockTableRva:X8}. " +
        "Imports, relocations and the original entry point are not reconstructed, so no runnable executable is produced."),
    };

    var caps = this.Capabilities | ExecutableUnpackCapabilities.CanBuildMemoryImage;
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadDecompressed, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static UnpackResult AddFailureDiagnostic(UnpackResult result, string failure) {
    if (string.IsNullOrEmpty(failure))
      return result;
    var diagnostics = result.Diagnostics.ToList();
    diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed, failure, true));
    return result with { Diagnostics = diagnostics };
  }
}

/// <summary>
/// Yoda's Protector locates its payload but does not decode it.
/// </summary>
/// <remarks>
/// The obstacle here is not execution. Walking the <c>.yP</c> stub shows the
/// same construction as its Crypter sibling — a plaintext prologue whose
/// <c>lodsb</c>/<c>stosb</c> loop decrypts the stub body — and the decrypted
/// body holds a per-section-class byte cipher plus an LZO1X decompressor
/// reading a four-byte uncompressed length ahead of the stream. Every step is
/// static. What is still missing is the source the stub restores the original
/// section names from: the packed section table is blank while both walkers
/// dispatch on the original names, so which cipher belongs to which section
/// cannot yet be decided. Until that is settled the handler locates and says
/// why, rather than guessing at a decryption.
/// </remarks>
public sealed class YodaProtectorExecutablePackerHandler : ProtectorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "yodaprotector";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Yoda's Protector";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) => name.Contains("yP", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "yoda"u8;

  /// <summary>
  /// Gets the static unpack obstacle.
  /// </summary>
  protected override string StaticUnpackObstacle =>
    "Yoda's Protector: payload located, not decoded. The stub is layered but static — a plaintext " +
    "prologue decrypts the stub body, which carries a per-section-class polymorphic byte cipher and " +
    "an LZO1X decompressor fed a four-byte uncompressed length — so this is not a case that needs " +
    "emulation. The unresolved step is where the stub restores the original section names from: the " +
    "packed section table is blank and both stub walkers dispatch on the original names, so the " +
    "cipher for a given section cannot yet be identified. No decryption is attempted rather than guessed.";
}

/// <summary>
/// Represents a yoda crypter executable packer handler.
/// </summary>
public sealed class YodaCrypterExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "yodacrypter";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Yoda's Crypter";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) =>
    name.Equals("yC", StringComparison.Ordinal) ||
    name.Equals(".yC", StringComparison.Ordinal);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "Yoda's"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s => IsPackerSection(s.Name));
    var hasLiteral = image.IndexOf(LiteralSignature) >= 0;
    return hasSection || hasLiteral
      ? new(true, this.Id, hasSection ? 0.93 : 0.82, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Yoda's Crypter marker was not found.", true)]);
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var payload = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => IsPackerSection(s.Name) && s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    if (payload.RawSize > 0) {
      var len = (int)Math.Min(payload.RawSize, (uint)(packed.OriginalImage.Length - payload.RawOffset));
      artifacts.Add(new("stub_section.bin", packed.OriginalImage.AsSpan((int)payload.RawOffset, len).ToArray(), "yodacrypter-stub"));
    }

    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (payload.RawSize > 0)
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64)
      caps |= ExecutableUnpackCapabilities.SupportsX64;

    var diagnostics = new List<ExecutableDiagnostic>();
    var level = payload.RawSize > 0 ? ExecutableUnpackLevel.PayloadLocated : ExecutableUnpackLevel.DetectionOnly;

    // Yoda's Crypter has to leave its per-build byte cipher in the stub in the
    // clear — the CPU executes it — so the encryption reverses statically.
    if (YodaCrypterStub.TryUnpack(packed.OriginalImage, out var stub) && stub is not null) {
      artifacts.Add(new("decrypted_image.bin", stub.DecryptedImage, "yodacrypter"));
      foreach (var (name, data) in EnumerateDecryptedSections(stub))
        artifacts.Add(new($"decrypted_sections/{SanitizeSectionName(name)}.bin", data, "yodacrypter"));

      level = ExecutableUnpackLevel.PayloadDecompressed;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload | ExecutableUnpackCapabilities.CanDecompressPayload;

      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        $"Yoda's Crypter encrypts in place instead of compressing. Sections [{string.Join(", ", stub.DecryptedSections)}] " +
        $"were decrypted with the {stub.SectionCipher.Count}-operation byte cipher read out of the stub; " +
        (stub.OriginalEntryPoint is { } entry
          ? $"the original entry point RVA 0x{entry:X8} was restored into the header. "
          : "the original entry point slot could not be located. ") +
        $"Sections [{string.Join(", ", stub.SkippedSections)}] are stored unencrypted."));
      diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed,
        "The result is the original section payload, not a loadable executable: Yoda's Crypter replaces the import " +
        "directory with its own descriptor format, nibble-swaps the hint/name strings, and discards any Authenticode " +
        "certificate or overlay, so a runnable rebuild is not attempted."));
    } else
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        "The Yoda's Crypter stub could not be walked: the entry-point prologue, the polymorphic cipher loop or the " +
        "section walker did not match the known layout, so no decryption was attempted.", true));

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static IEnumerable<(string Name, byte[] Data)> EnumerateDecryptedSections(YodaCrypterStubInfo stub) {
    foreach (var s in PackerScanner.GetPeSectionRanges(stub.DecryptedImage)) {
      if (s.RawSize == 0 || !stub.DecryptedSections.Contains(s.Name))
        continue;
      if (s.RawOffset + s.RawSize > (uint)stub.DecryptedImage.Length)
        continue;
      yield return (s.Name, stub.DecryptedImage.AsSpan((int)s.RawOffset, (int)s.RawSize).ToArray());
    }
  }

  private static string SanitizeSectionName(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "section" : sb.ToString();
  }
}

/// <summary>
/// Represents a themida executable packer handler.
/// </summary>
public sealed class ThemidaExecutablePackerHandler : ProtectorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "themida";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Themida";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) =>
    name.Contains("themida", StringComparison.OrdinalIgnoreCase) ||
    name.Equals(".boot", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => [] ;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasThemidaSection = sections.Any(s => s.Name.Contains("themida", StringComparison.OrdinalIgnoreCase));
    var hasBootSection = sections.Any(s => s.Name.Equals(".boot", StringComparison.OrdinalIgnoreCase));
    var emptyCount = sections.Count(s => string.IsNullOrWhiteSpace(s.Name));
    if (hasThemidaSection || (hasBootSection && emptyCount >= 2))
      return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

/// <summary>
/// Represents a telock executable packer handler.
/// </summary>
public sealed class TelockExecutablePackerHandler : ProtectorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "telock";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "TELock";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) =>
    string.IsNullOrWhiteSpace(name) ||
    name.Contains("tElock", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "tElock"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasLiteral = image.IndexOf("tElock"u8) >= 0 || image.IndexOf("TELock"u8) >= 0;
    var hasSection = sections.Any(s => s.Name.Contains("tElock", StringComparison.OrdinalIgnoreCase));
    if (hasLiteral || hasSection) return new(true, this.Id, 0.85, []);
    // Purely-structural fallback (blank last section with the entry point inside
    // it) is a weak signal shared by other blank-sectioned packers. Never let it
    // swallow an FSG image: FSG's "FSG!" stub marker and its t/ta/a (or blank
    // three-section) layout must route to the dedicated FSG handler, not here.
    if (!LooksLikeFsg(image, sections) && HasTelockBlankEntrySection(image))
      return new(true, this.Id, 0.55, []);
    return new(false, this.Id, 0, []);
  }

  private static bool LooksLikeFsg(ReadOnlySpan<byte> image, IReadOnlyList<(string Name, uint Characteristics)> sections) {
    if (PackerScanner.IndexOfBounded(image, "FSG!"u8, 0x4000) >= 0)
      return true;
    if (sections.Count != 3)
      return false;
    var emptyCount = sections.Count(s => string.IsNullOrWhiteSpace(s.Name));
    var hasTa = sections.Any(s => s.Name.Equals("ta", StringComparison.OrdinalIgnoreCase));
    var hasA = sections.Any(s => s.Name.Equals("a", StringComparison.OrdinalIgnoreCase));
    return emptyCount >= 2 || (hasTa && hasA);
  }

  private static bool HasTelockBlankEntrySection(ReadOnlySpan<byte> image) {
    if (image.Length < 0x40)
      return false;
    var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..]);
    if (peOffset < 0 || peOffset + 24 > image.Length)
      return false;
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 6)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 20)..]);
    if (sectionCount < 3)
      return false;
    var optionalOffset = peOffset + 24;
    if (optionalOffset + 20 > image.Length)
      return false;
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(image[(optionalOffset + 16)..]);
    var sectionOffset = optionalOffset + optionalSize;
    var lastOffset = sectionOffset + (sectionCount - 1) * 40;
    if (lastOffset + 40 > image.Length)
      return false;

    var lastName = Encoding.ASCII.GetString(image.Slice(lastOffset, 8)).TrimEnd('\0');
    var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(lastOffset + 8)..]);
    var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(image[(lastOffset + 12)..]);
    var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(lastOffset + 16)..]);
    if (!string.IsNullOrWhiteSpace(lastName) || rawSize == 0)
      return false;

    return entry >= virtualAddress && entry < virtualAddress + Math.Max(virtualSize, rawSize);
  }
}

/// <summary>
/// Represents a win upack fallback executable packer handler.
/// </summary>
public sealed class WinUpackFallbackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "winupackfallback";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "WinUpack";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) => name.Contains("Upack", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "Upack"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasLiteral = image.IndexOf("Upack"u8) >= 0 || image.IndexOf("By Dwing"u8) >= 0;
    var hasSection = sections.Any(s => s.Name.Contains("Upack", StringComparison.OrdinalIgnoreCase));
    var hasPs = sections.Count > 0 && sections[0].Name.StartsWith("PS");
    if (hasLiteral || hasSection || hasPs) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

/// <summary>
/// squishy (Jake "ferris" Taylor / logicoma, 2016+, <c>https://logicoma.io/squishy</c>) is a
/// closed-source Win32 PE compressor purpose-built for demoscene 64K intros. Its own release
/// notes describe an adaptive context-mixing coder ("context modeling" drawing on PAQ and LZMA
/// literature) bootstrapped from "a crinkler-like model", plus a state-based disassembler that
/// transforms jmp/call instructions ahead of coding — the same closed, non-LZ category as
/// Crinkler and kkrunchy, not a publicly specified format.
///
/// Detection was confirmed against real output from the official releases (squishy-0.1.3, x86,
/// and squishy-0.2.0, x86-64): the packed PE always has exactly one section literally named
/// <c>logicoma</c>, and the DOS-stub region ahead of the (deliberately tiny) <c>e_lfanew</c>
/// embeds the same "logicoma" text in every build, plus an ASCII-art "squished by ...
/// ferris@logicoma" credit banner starting with 0.2.0.
/// </summary>
public sealed class SquishyExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "squishy";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "squishy";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) => name.Equals("logicoma", StringComparison.Ordinal);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "logicoma"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "Not a valid PE.", true)]);

    var sections = PackerScanner.GetPeSections(image);
    var hasSection = sections.Any(s => IsPackerSection(s.Name));
    // The "logicoma"/"squished by" credit text sits in the DOS-stub area ahead of squishy's
    // own tiny e_lfanew; bound the literal scan to the header so an unrelated file that merely
    // mentions "logicoma" somewhere in its data section doesn't false-positive.
    var hasHeaderLiteral =
      PackerScanner.IndexOfBounded(image, LiteralSignature, 0x400) >= 0 ||
      PackerScanner.IndexOfBounded(image, "squished by"u8, 0x400) >= 0;

    return hasSection || hasHeaderLiteral
      ? new(true, this.Id, hasSection ? 0.92 : 0.8, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "squishy signature not found.", true)]);
  }

  /// <summary>
  /// squishy's payload is coded by an undocumented, closed context-mixing model — there is no
  /// public specification or reference decoder to statically reverse it, so this handler never
  /// runs the generic aPLib/NRV probes (a spurious "clean" decode against a context-mixed stream
  /// would fabricate a decompression that isn't actually happening). It honestly stops at
  /// locating the single named payload section.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;

    foreach (var s in PackerScanner.GetPeSectionRanges(packed.OriginalImage)) {
      if (!IsPackerSection(s.Name) || s.RawSize == 0 || s.RawOffset >= packed.OriginalImage.Length)
        continue;
      var len = (int)Math.Min(s.RawSize, (uint)(packed.OriginalImage.Length - s.RawOffset));
      artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)s.RawOffset, len).ToArray(), "stored"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      break;
    }

    diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
      "squishy: closed demoscene compressor; requires its runtime depacker. squishy uses an " +
      "undocumented adaptive context-mixing coder (PAQ/LZMA-inspired) with a state-based " +
      "disassembler transform ahead of coding, so no public specification or reference decoder " +
      "exists to statically reverse the payload.", true));

    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;
    else if (packed.ImageInfo?.Architecture == CpuArchitecture.X64) caps |= ExecutableUnpackCapabilities.SupportsX64;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}

/// <summary>
/// Represents a fsg fallback executable packer handler.
/// </summary>
public sealed class FsgFallbackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "fsgfallback";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "FSG";
  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) =>
    name.Equals("ta", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("a", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("fsg", StringComparison.OrdinalIgnoreCase);
  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "FSG!"u8;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var emptyCount = sections.Count(s => string.IsNullOrWhiteSpace(s.Name));
    var baseMatch = image.IndexOf(LiteralSignature) >= 0;
    if (baseMatch || emptyCount >= 2) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var generic = base.Unpack(packed, options);
    if (generic.Level >= ExecutableUnpackLevel.PayloadLocated)
      return generic;

    var section = PackerScanner.GetPeSectionRanges(packed.OriginalImage)
      .Where(s => s.RawSize > 0 && s.RawOffset < packed.OriginalImage.Length)
      .OrderByDescending(s => s.RawSize)
      .FirstOrDefault();
    if (section.RawSize == 0)
      return generic;

    var artifacts = generic.Artifacts
      .Where(a => a.Name != "diagnostics.json")
      .ToList();
    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "FSG payload section was located by structural fallback, but the managed FSG transform/decompression path did not decode it.",
        true),
    };
    var len = (int)Math.Min(section.RawSize, (uint)(packed.OriginalImage.Length - section.RawOffset));
    artifacts.Add(new("compressed_payload.bin", packed.OriginalImage.AsSpan((int)section.RawOffset, len).ToArray(), "fsg-section"));

    var caps = ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }
}
