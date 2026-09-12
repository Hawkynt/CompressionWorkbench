#pragma warning disable CS1591
using System.Diagnostics.CodeAnalysis;

namespace FileSystem.TahoeLafs;

/// <summary>The broad object family represented by a Tahoe-LAFS capability.</summary>
public enum TahoeLafsCapabilityKind {
  Unknown,
  File,
  Directory,
  Verifier,
}

/// <summary>The authority conveyed by a Tahoe-LAFS capability.</summary>
public enum TahoeLafsCapabilityAccess {
  ReadOnly,
  ReadWrite,
  Verify,
}

/// <summary>The Tahoe encoding/profile named by a known capability family.</summary>
public enum TahoeLafsObjectFormat {
  Unknown,
  Chk,
  Lit,
  Sdmf,
  Mdmf,
}

/// <summary>
/// Parsed Tahoe-LAFS capability. <see cref="Value"/> is a bearer secret for read
/// and especially write capabilities; callers must not log or display it.
/// </summary>
public sealed class TahoeLafsCapability {
  private TahoeLafsCapability(
      string value,
      TahoeLafsCapabilityKind kind,
      TahoeLafsCapabilityAccess access,
      TahoeLafsObjectFormat format,
      bool isDirectory) {
    this.Value = value;
    this.Kind = kind;
    this.Access = access;
    this.Format = format;
    this.IsDirectory = isDirectory;
  }

  /// <summary>The literal capability string. Treat this as a bearer credential.</summary>
  public string Value { get; }

  public TahoeLafsCapabilityKind Kind { get; }
  public TahoeLafsCapabilityAccess Access { get; }
  public TahoeLafsObjectFormat Format { get; }
  public bool IsDirectory { get; }
  public bool CanRead => this.Access is TahoeLafsCapabilityAccess.ReadOnly or TahoeLafsCapabilityAccess.ReadWrite;
  public bool CanWrite => this.Access == TahoeLafsCapabilityAccess.ReadWrite;
  public bool IsMutable => this.Format is TahoeLafsObjectFormat.Sdmf or TahoeLafsObjectFormat.Mdmf;

  /// <summary>Parses a capability without exposing its value in error text.</summary>
  public static TahoeLafsCapability Parse(string value) {
    if (!TryParse(value, out var capability))
      throw new FormatException("Invalid Tahoe-LAFS capability.");
    return capability;
  }

  /// <summary>
  /// Recognizes the capability families Tahoe-LAFS currently publishes. Unknown
  /// future read-only wrappers remain pass-through values instead of being
  /// destructively stripped or reinterpreted.
  /// </summary>
  public static bool TryParse(string? value, [NotNullWhen(true)] out TahoeLafsCapability? capability) {
    capability = null;
    if (string.IsNullOrWhiteSpace(value) || value.Length > 16 * 1024)
      return false;
    if (value.Any(char.IsControl))
      return false;

    var (kind, access, format, directory) = Classify(value);
    if (kind == TahoeLafsCapabilityKind.Unknown
        && !value.StartsWith("URI:", StringComparison.Ordinal)
        && !value.StartsWith("ro.", StringComparison.Ordinal)
        && !value.StartsWith("imm.", StringComparison.Ordinal))
      return false;

    capability = new(value, kind, access, format, directory);
    return true;
  }

  private static (TahoeLafsCapabilityKind Kind, TahoeLafsCapabilityAccess Access, TahoeLafsObjectFormat Format, bool Directory) Classify(string value)
    => value switch {
      _ when value.StartsWith("URI:DIR2-MDMF-Verifier:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Verifier, TahoeLafsCapabilityAccess.Verify, TahoeLafsObjectFormat.Mdmf, true),
      _ when value.StartsWith("URI:DIR2-MDMF-RO:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Mdmf, true),
      _ when value.StartsWith("URI:DIR2-MDMF:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Mdmf, true),
      _ when value.StartsWith("URI:DIR2-CHK-Verifier:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Verifier, TahoeLafsCapabilityAccess.Verify, TahoeLafsObjectFormat.Chk, true),
      _ when value.StartsWith("URI:DIR2-CHK:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Chk, true),
      _ when value.StartsWith("URI:DIR2-LIT:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Lit, true),
      _ when value.StartsWith("URI:DIR2-Verifier:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Verifier, TahoeLafsCapabilityAccess.Verify, TahoeLafsObjectFormat.Sdmf, true),
      _ when value.StartsWith("URI:DIR2-RO:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Sdmf, true),
      _ when value.StartsWith("URI:DIR2:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Sdmf, true),
      _ when value.StartsWith("URI:MDMF-Verifier:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Verifier, TahoeLafsCapabilityAccess.Verify, TahoeLafsObjectFormat.Mdmf, false),
      _ when value.StartsWith("URI:MDMF-RO:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Mdmf, false),
      _ when value.StartsWith("URI:MDMF:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Mdmf, false),
      _ when value.StartsWith("URI:SSK-Verifier:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Verifier, TahoeLafsCapabilityAccess.Verify, TahoeLafsObjectFormat.Sdmf, false),
      _ when value.StartsWith("URI:SSK-RO:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Sdmf, false),
      _ when value.StartsWith("URI:SSK:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Sdmf, false),
      _ when value.StartsWith("URI:CHK-Verifier:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Verifier, TahoeLafsCapabilityAccess.Verify, TahoeLafsObjectFormat.Chk, false),
      _ when value.StartsWith("URI:CHK:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Chk, false),
      _ when value.StartsWith("URI:LIT:", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Lit, false),
      _ when value.StartsWith("ro.", StringComparison.Ordinal) || value.StartsWith("imm.", StringComparison.Ordinal)
        => (TahoeLafsCapabilityKind.Unknown, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Unknown, false),
      _ => (TahoeLafsCapabilityKind.Unknown, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Unknown, false),
    };

  /// <summary>Returns only non-secret classification data.</summary>
  public override string ToString()
    => $"Tahoe-LAFS {this.Kind} ({this.Access}, {this.Format})";
}
