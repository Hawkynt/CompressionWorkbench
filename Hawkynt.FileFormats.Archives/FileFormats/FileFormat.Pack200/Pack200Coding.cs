namespace FileFormat.Pack200;

/// <summary>
/// A Pack200 (JSR-200) band coding, described by its (B, H, S, D) parameters.
/// </summary>
/// <remarks>
/// <para>
/// Pack200 encodes each band of integers with a variable-length "BHSD" coding as
/// defined in the JSR-200 specification, section 5.4:
/// </para>
/// <list type="bullet">
///   <item><description><b>B</b> — maximum number of bytes per value (1..5).</description></item>
///   <item><description><b>H</b> — the "high" radix (1..256); <c>L = 256 - H</c> byte values act as terminators.</description></item>
///   <item><description><b>S</b> — signedness (0 = unsigned, 1 or 2 = number of low sign bits).</description></item>
///   <item><description><b>D</b> — delta flag (0 = literal values, 1 = running sum of the coded deltas).</description></item>
/// </list>
/// </remarks>
/// <param name="B">Maximum bytes per value.</param>
/// <param name="H">High radix.</param>
/// <param name="S">Signedness (0, 1 or 2).</param>
/// <param name="D">Delta flag (0 or 1).</param>
public readonly record struct Pack200Coding(int B, int H, int S, int D) {

  /// <summary>Number of low byte values (0..L-1) that terminate a coded value.</summary>
  public int L => 256 - this.H;

  // ── Canonical codings used by the archive header and default band layout ──

  /// <summary>UNSIGNED5 (B=5, H=64, S=0, D=0): the default coding for most count/index bands.</summary>
  public static readonly Pack200Coding Unsigned5 = new(5, 64, 0, 0);

  /// <summary>SIGNED5 (B=5, H=64, S=1, D=0): signed literal values.</summary>
  public static readonly Pack200Coding Signed5 = new(5, 64, 1, 0);

  /// <summary>DELTA5 (B=5, H=64, S=1, D=1): signed running-delta references (e.g. cp_Utf8_prefix, class_this).</summary>
  public static readonly Pack200Coding Delta5 = new(5, 64, 1, 1);

  /// <summary>UDELTA5 (B=5, H=64, S=0, D=1): unsigned running-delta references (e.g. cp_Class, cp_String).</summary>
  public static readonly Pack200Coding Udelta5 = new(5, 64, 0, 1);

  /// <summary>BYTE1 (B=1, H=256, S=0, D=0): a single raw byte.</summary>
  public static readonly Pack200Coding Byte1 = new(1, 256, 0, 0);

  /// <summary>CHAR3 (B=3, H=128, S=0, D=0): the default coding for cp_Utf8 characters.</summary>
  public static readonly Pack200Coding Char3 = new(3, 128, 0, 0);
}
