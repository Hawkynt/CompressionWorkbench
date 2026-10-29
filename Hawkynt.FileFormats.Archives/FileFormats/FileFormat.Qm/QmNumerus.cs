namespace FileFormat.Qm;

/// <summary>
/// The plural-rule programs Qt embeds in a QM file, and which language gets which. The byte codes are
/// QTranslator's (qtranslator_p.h); the rule sets, their form counts and their language assignments are
/// the constants lrelease uses (qttools numerus.cpp), copied exactly — see docs/QM-ON-DISK.md.
/// </summary>
internal static class QmNumerus {

  private const byte Eq = 0x01, Lt = 0x02, Leq = 0x03, Between = 0x04, Not = 0x08, Mod10 = 0x10, Mod100 = 0x20;
  private const byte And = 0xFD, Or = 0xFE, NewRule = 0xFF;
  private const byte Neq = Not | Eq, Geq = Not | Lt, NotBetween = Not | Between;

  private sealed record RuleSet(byte[] Rules, int Forms, string[] Languages);

  // Order matters: lrelease takes the first set that lists the language (Filipino is French style).
  private static readonly RuleSet[] Sets = [
    new([], 1, ["bi", "my", "zh", "dz", "fj", "gn", "hu", "id", "ja", "jv", "ko", "ms", "na", "om", "fa", "su", "tt", "th", "bo", "tr", "vi", "yo", "za"]),
    new([Eq, 1], 2, ["ab", "aa", "af", "sq", "am", "as", "ay", "az", "ba", "eu", "bn", "bg", "ca", "kw", "co", "da", "nl", "en", "eo", "et",
      "fo", "fi", "fur", "fy", "gl", "lg", "ka", "de", "el", "kl", "gu", "ha", "he", "hi", "ia", "ie", "it", "kn", "ks", "kk", "km", "rw",
      "ky", "ku", "lo", "la", "ln", "lb", "mg", "ml", "mr", "mn", "ne", "nso", "nb", "no", "nn", "oc", "or", "ps", "pt", "pa", "qu", "rm", "rn",
      "sn", "sd", "si", "so", "st", "es", "sw", "ss", "sv", "tg", "ta", "te", "to", "ts", "tn", "tk", "ug", "ur", "uz", "vo", "wo", "xh",
      "yi", "zu"]),
    new([Leq, 1], 2, ["hy", "br", "fr", "pt_BR", "fil", "ti", "wa"]),
    new([Mod10 | Eq, 1, And, Mod100 | Neq, 11, NewRule, Neq, 0], 3, ["lv"]),
    new([Mod10 | Eq, 1, And, Mod100 | Neq, 11], 2, ["is"]),
    new([Eq, 1, NewRule, Eq, 2], 3, ["dv", "iu", "ik", "ga", "gv", "mi", "se", "sm", "sa"]),
    new([Eq, 1, Or, Eq, 11, NewRule, Eq, 2, Or, Eq, 12, NewRule, Between, 3, 19], 4, ["gd"]),
    new([Eq, 1, NewRule, Between, 2, 4], 3, ["sk", "cs"]),
    new([Mod10 | Eq, 1, NewRule, Mod10 | Eq, 2], 3, ["mk"]),
    new([Mod10 | Eq, 1, And, Mod100 | Neq, 11, NewRule, Mod10 | Neq, 0, And, Mod100 | NotBetween, 10, 19], 3, ["lt"]),
    new([Mod10 | Eq, 1, And, Mod100 | Neq, 11, NewRule, Mod10 | Between, 2, 4, And, Mod100 | NotBetween, 10, 19], 3, ["bs", "be", "hr", "ru", "sr", "uk"]),
    new([Eq, 1, NewRule, Mod10 | Between, 2, 4, And, Mod100 | NotBetween, 10, 19], 3, ["pl"]),
    new([Eq, 1, NewRule, Eq, 0, Or, Mod100 | Between, 1, 19], 3, ["ro"]),
    new([Mod100 | Eq, 1, NewRule, Mod100 | Eq, 2, NewRule, Mod100 | Between, 3, 4], 4, ["sl"]),
    new([Eq, 1, NewRule, Eq, 0, Or, Mod100 | Between, 1, 10, NewRule, Mod100 | Between, 11, 19], 4, ["mt"]),
    new([Eq, 0, NewRule, Eq, 1, NewRule, Between, 2, 5, NewRule, Eq, 6], 5, ["cy"]),
    new([Eq, 0, NewRule, Eq, 1, NewRule, Eq, 2, NewRule, Mod100 | Between, 3, 10, NewRule, Mod100 | Geq, 11], 6, ["ar"]),
  ];

  /// <summary>
  /// The rule program and form count lrelease uses for a TS <c>language</c> code (<c>de</c>, <c>de_DE</c>,
  /// <c>pt-BR</c>), or null for a language it has no rules for. A bare <c>pt</c> means Brazil, Qt's default
  /// territory for Portuguese.
  /// </summary>
  public static (byte[] Rules, int Forms)? For(string? languageCode) {
    if (string.IsNullOrEmpty(languageCode)) return null;
    var separator = languageCode.IndexOf('_');
    if (separator < 0) separator = languageCode.IndexOf('-');
    var language = separator < 0 ? languageCode : languageCode[..separator];
    var territory = separator < 0 ? (language == "pt" ? "BR" : null) : languageCode[(separator + 1)..];
    if (territory != null)
      foreach (var set in Sets)
        if (set.Languages.Contains(language + "_" + territory)) return (set.Rules, set.Forms);
    foreach (var set in Sets)
      if (set.Languages.Contains(language)) return (set.Rules, set.Forms);
    return null;
  }
}
