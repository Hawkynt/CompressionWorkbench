#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

public abstract class PlannedExecutablePackerHandlerBase : IExecutablePackerHandler {
  public abstract string Id { get; }
  public abstract string DisplayName { get; }

  public virtual ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  protected abstract bool IsPackerSection(string name);
  protected abstract ReadOnlySpan<byte> LiteralSignature { get; }

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
    foreach (var s in sections) {
      if (IsPackerSection(s.Name) && s.RawSize > 0) {
        var len = (int)Math.Min(s.RawSize, (uint)(packed.OriginalImage.Length - s.RawOffset));
        var data = packed.OriginalImage.AsSpan((int)s.RawOffset, len).ToArray();
        artifacts.Add(new($"packed_section_{s.Name}.bin", data, "stored"));
        level = ExecutableUnpackLevel.PayloadLocated;
        caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      }
    }

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
        $"{this.DisplayName} payload located. Managed decompression/transform reversal remains planned.", true));
    }

    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

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
public abstract class ProtectorExecutablePackerHandlerBase : PlannedExecutablePackerHandlerBase {
  public override ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

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

    diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
      $"{this.DisplayName}: static full unpack not feasible (runtime protector). The original " +
      "image is reconstructed only by executing the anti-debug / code-virtualization stub, so the " +
      "handler stops at payload location; no managed decompression is attempted.", true));

    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>An RWX or entry-bearing section of a protector still counts as the located protected body.</summary>
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

public sealed class AlienyzeExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "alienyze";
  public override string DisplayName => "Alienyze";
  protected override bool IsPackerSection(string name) => name.Contains("alien", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "Alienyze"u8;
}

public sealed class AmberExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "amber";
  public override string DisplayName => "Amber reflective PE loader";
  protected override bool IsPackerSection(string name) => false;
  protected override ReadOnlySpan<byte> LiteralSignature => "amber"u8;

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
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var level = ExecutableUnpackLevel.DetectionOnly;
    var caps = ExecutableUnpackCapabilities.CanDetect;

    var embedded = FindEmbeddedPe(image);
    if (embedded is { } pe) {
      artifacts.Add(new("embedded_pe.bin", pe, "stored"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        "Amber: a plaintext embedded PE was carved from the reflective payload; " +
        "runtime relocation/import fix-ups applied by the loader are not replayed."));
    } else {
      var region = LargestHighEntropyRegion(image);
      if (region is { } r) {
        artifacts.Add(new("reflective_payload.bin", image.AsSpan(r.Offset, r.Length).ToArray(), "stored"));
        level = ExecutableUnpackLevel.PayloadLocated;
        caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      }
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "Amber: reflective payload located but the embedded PE is obfuscated (XOR/RC4 keyed by the " +
        "shellcode stub); static decryption is not feasible without executing the loader.", true));
    }

    caps |= ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;

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

  private static (int Offset, int Length)? LargestHighEntropyRegion(byte[] image) {
    (int Offset, int Length)? best = null;
    foreach (var s in PackerScanner.GetPeSectionRanges(image)) {
      if (s.RawSize < 256 || s.RawOffset >= image.Length) continue;
      var len = (int)Math.Min(s.RawSize, (uint)(image.Length - s.RawOffset));
      if (best is null || len > best.Value.Length)
        best = ((int)s.RawOffset, len);
    }
    return best;
  }
}

public sealed class BeRoExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "beroexepacker";
  public override string DisplayName => "BeRoEXEPacker";
  protected override bool IsPackerSection(string name) =>
    name.Contains("bero", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("gu_idata", StringComparison.Ordinal) ||
    name.Equals("gu_rsrc", StringComparison.Ordinal);
  protected override ReadOnlySpan<byte> LiteralSignature => "BeRo"u8;
}

public sealed class EronanaExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "eronanapacker";
  public override string DisplayName => "Eronana Packer";
  protected override bool IsPackerSection(string name) => name.Contains("eron", StringComparison.OrdinalIgnoreCase) || name.Equals(".packer", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "Eronana"u8;
}

public sealed class Exe32packExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "exe32pack";
  public override string DisplayName => "Exe32pack";
  protected override bool IsPackerSection(string name) => false;
  protected override ReadOnlySpan<byte> LiteralSignature => "exe32pack"u8;
  
  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasExe32 = image.IndexOf("exe32pack"u8) >= 0;
    var hasKnownSection = sections.Any(s => s.Name == ".i" || s.Name == ".f" || s.Name == ".c" || s.Name == ".v" || s.Name == ".h");
    if (hasExe32 || hasKnownSection) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

public sealed class ExpressorExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "expressor";
  public override string DisplayName => "EXpressor";
  protected override bool IsPackerSection(string name) =>
    name.Contains("exp", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("ex_", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "EXpressor"u8;
}

public sealed class JdpackExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "jdpack";
  public override string DisplayName => "JDPack";
  protected override bool IsPackerSection(string name) => name.Contains("jd", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "JDPack"u8;
}

public sealed class MoleboxExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "molebox";
  public override string DisplayName => "Molebox";
  protected override bool IsPackerSection(string name) =>
    name.Contains("mole", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("mbx", StringComparison.OrdinalIgnoreCase) ||
    int.TryParse(name, out _);
  protected override ReadOnlySpan<byte> LiteralSignature => "Molebox"u8;
}

public sealed class NeoliteExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "neolite";
  public override string DisplayName => "Neolite";
  protected override bool IsPackerSection(string name) => name.Contains("neolit", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "NeoLite"u8;
}

public sealed class YodaProtectorExecutablePackerHandler : ProtectorExecutablePackerHandlerBase {
  public override string Id => "yodaprotector";
  public override string DisplayName => "Yoda's Protector";
  protected override bool IsPackerSection(string name) => name.Contains("yP", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "yoda"u8;
}

public sealed class ThemidaFallbackExecutablePackerHandler : ProtectorExecutablePackerHandlerBase {
  public override string Id => "themidafallback";
  public override string DisplayName => "Themida";
  protected override bool IsPackerSection(string name) => name.Contains("themida", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => [] ;
}

public sealed class TelockExecutablePackerHandler : ProtectorExecutablePackerHandlerBase {
  public override string Id => "telock";
  public override string DisplayName => "TELock";
  protected override bool IsPackerSection(string name) => name.Contains("tElock", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "tElock"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var hasLiteral = image.IndexOf("tElock"u8) >= 0 || image.IndexOf("TELock"u8) >= 0;
    var hasSection = sections.Any(s => s.Name.Contains("tElock", StringComparison.OrdinalIgnoreCase));
    var hasEmptyLast = sections.Count > 0 && string.IsNullOrWhiteSpace(sections[sections.Count - 1].Name);
    if (hasLiteral || hasSection || hasEmptyLast) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}

public sealed class WinUpackFallbackExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "winupackfallback";
  public override string DisplayName => "WinUpack";
  protected override bool IsPackerSection(string name) => name.Contains("Upack", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "Upack"u8;

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

public sealed class FsgFallbackExecutablePackerHandler : PlannedExecutablePackerHandlerBase {
  public override string Id => "fsgfallback";
  public override string DisplayName => "FSG";
  protected override bool IsPackerSection(string name) => false;
  protected override ReadOnlySpan<byte> LiteralSignature => "FSG!"u8;

  public override DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image)) return new(false, this.Id, 0, []);
    var sections = PackerScanner.GetPeSections(image);
    var emptyCount = sections.Count(s => string.IsNullOrWhiteSpace(s.Name));
    var baseMatch = image.IndexOf(LiteralSignature) >= 0;
    if (baseMatch || emptyCount >= 2) return new(true, this.Id, 0.85, []);
    return new(false, this.Id, 0, []);
  }
}
