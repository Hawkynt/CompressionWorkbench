namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor publishes a list of tunable knobs the
/// user can adjust before the format is written. Drives the
/// "Convert Archive" target-options dialog in the UI and the CLI's
/// <c>--opt key=value</c> flag.
///
/// <para>Implementations should return a stable list of
/// <see cref="FormatOptionDescriptor"/>s describing each knob. The dialog
/// / CLI collects user values into <see cref="FormatCreateOptions.FormatSpecific"/>
/// keyed by <see cref="FormatOptionDescriptor.Key"/>; the writer reads them
/// back out in <c>Create()</c>.</para>
///
/// <para>Descriptors that don't implement this surface get the default
/// "no extra knobs" experience.</para>
/// </summary>
public interface IFormatOptionsSchema {
  /// <summary>The set of knobs this format exposes. Empty list = no extra options.</summary>
  IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; }
}

/// <summary>How a <see cref="FormatOptionDescriptor"/> renders + parses.</summary>
public enum FormatOptionKind {
  /// <summary>Free-form text (e.g. volume label).</summary>
  String,
  /// <summary>Integer (e.g. cluster size in bytes). <see cref="FormatOptionDescriptor.AllowedValues"/> renders as a dropdown of preset sizes.</summary>
  Integer,
  /// <summary>Boolean toggle (e.g. "enable journal").</summary>
  Boolean,
  /// <summary>One of a fixed enumerated set (e.g. FAT12 / FAT16 / FAT32). <see cref="FormatOptionDescriptor.AllowedValues"/> is mandatory.</summary>
  Enum,
}

/// <summary>
/// Describes one tunable knob. The dialog / CLI uses
/// <see cref="DisplayName"/> for the label, <see cref="Description"/> as
/// hover-tip help, <see cref="Default"/> as the initial value, and
/// <see cref="AllowedValues"/> to constrain the input where applicable.
/// </summary>
/// <param name="Key">Stable machine-readable key; used as the
/// <see cref="FormatCreateOptions.FormatSpecific"/> dictionary key. Convention: PascalCase.</param>
/// <param name="DisplayName">UI label.</param>
/// <param name="Kind">How to render + parse.</param>
/// <param name="Default">Initial value, in canonical string form (e.g. "0" for "auto", "Auto" for an enum).</param>
/// <param name="AllowedValues">For <see cref="FormatOptionKind.Enum"/>: mandatory list of allowed values.
/// For <see cref="FormatOptionKind.Integer"/>: optional preset list (renders as dropdown rather than text box).
/// For other kinds: null.</param>
/// <param name="Description">Hover-tip help.</param>
/// <param name="DependsOn">Optional gate: only show this knob if another knob's current
/// value matches one of these. Format: <c>"OtherKey=value1|value2"</c>. Used for cascading
/// options (e.g. "Journal" only visible when "Version" is ext3/ext4).</param>
public sealed record FormatOptionDescriptor(
    string Key,
    string DisplayName,
    FormatOptionKind Kind,
    string Default,
    IReadOnlyList<string>? AllowedValues = null,
    string? Description = null,
    string? DependsOn = null);
