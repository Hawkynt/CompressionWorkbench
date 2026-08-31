using static Hawkynt.Algorithms.Checksums.CheckDigitHelpers;

namespace Hawkynt.Algorithms.Checksums;

/// <summary>Luhn modulo-10 check digit.</summary>
public static class Luhn {
  /// <summary>
  /// Determines whether the supplied value has a valid Luhn.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> digits) {
    if (digits.IsEmpty)
      return false;

    var sum = 0;
    var doubleDigit = false;
    for (var i = digits.Length - 1; i >= 0; --i) {
      var c = digits[i];
      if (c is < '0' or > '9')
        return false;
      var digit = c - '0';
      if (doubleDigit) {
        digit *= 2;
        if (digit > 9)
          digit -= 9;
      }
      sum += digit;
      doubleDigit = !doubleDigit;
    }
    return sum % 10 == 0;
  }

  /// <summary>
  /// Generates the Luhn check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> payload) {
    var sum = 0;
    var doubleDigit = true;
    for (var i = payload.Length - 1; i >= 0; --i) {
      var c = payload[i];
      if (c is < '0' or > '9')
        throw new ArgumentException("Payload must contain decimal digits only.", nameof(payload));
      var digit = c - '0';
      if (doubleDigit) {
        digit *= 2;
        if (digit > 9)
          digit -= 9;
      }
      sum += digit;
      doubleDigit = !doubleDigit;
    }
    return (10 - sum % 10) % 10;
  }
}

/// <summary>Verhoeff check digit using the dihedral group D5 tables.</summary>
public static class Verhoeff {
  private static readonly byte[,] Multiplication = {
    {0,1,2,3,4,5,6,7,8,9}, {1,2,3,4,0,6,7,8,9,5},
    {2,3,4,0,1,7,8,9,5,6}, {3,4,0,1,2,8,9,5,6,7},
    {4,0,1,2,3,9,5,6,7,8}, {5,9,8,7,6,0,4,3,2,1},
    {6,5,9,8,7,1,0,4,3,2}, {7,6,5,9,8,2,1,0,4,3},
    {8,7,6,5,9,3,2,1,0,4}, {9,8,7,6,5,4,3,2,1,0}
  };

  private static readonly byte[,] Permutation = {
    {0,1,2,3,4,5,6,7,8,9}, {1,5,7,6,2,8,3,0,9,4},
    {5,8,0,3,7,9,6,1,4,2}, {8,9,1,6,0,4,3,5,2,7},
    {9,4,5,3,1,2,6,8,7,0}, {4,2,8,6,5,7,3,9,0,1},
    {2,7,9,3,8,0,6,4,1,5}, {7,0,4,6,9,1,3,2,5,8}
  };

  private static readonly byte[] Inverse = [0,4,3,2,1,5,6,7,8,9];

  /// <summary>
  /// Determines whether the supplied value has a valid Verhoeff.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> digits) {
    if (digits.IsEmpty)
      return false;

    var c = 0;
    for (var position = 0; position < digits.Length; ++position) {
      var ch = digits[digits.Length - 1 - position];
      if (ch is < '0' or > '9')
        return false;
      c = Multiplication[c, Permutation[position % 8, ch - '0']];
    }
    return c == 0;
  }

  /// <summary>
  /// Generates the Verhoeff check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> payload) {
    var c = 0;
    for (var position = 0; position < payload.Length; ++position) {
      var ch = payload[payload.Length - 1 - position];
      if (ch is < '0' or > '9')
        throw new ArgumentException("Payload must contain decimal digits only.", nameof(payload));
      c = Multiplication[c, Permutation[(position + 1) % 8, ch - '0']];
    }
    return Inverse[c];
  }
}

/// <summary>Damm check digit using the standard anti-symmetric quasigroup.</summary>
public static class Damm {
  private static readonly byte[,] Table = {
    {0,3,1,7,5,9,8,6,4,2}, {7,0,9,2,1,5,4,8,6,3},
    {4,2,0,6,8,7,1,3,5,9}, {1,7,5,0,9,8,3,4,2,6},
    {6,1,2,3,0,4,5,9,7,8}, {3,6,7,4,2,0,9,5,8,1},
    {5,8,6,9,7,2,0,1,3,4}, {8,9,4,5,3,6,2,0,1,7},
    {9,4,3,8,6,1,7,2,0,5}, {2,5,8,1,4,3,6,7,9,0}
  };

  /// <summary>
  /// Determines whether the supplied value has a valid Damm.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> digits) {
    if (digits.IsEmpty)
      return false;
    var interim = 0;
    foreach (var ch in digits) {
      if (ch is < '0' or > '9')
        return false;
      interim = Table[interim, ch - '0'];
    }
    return interim == 0;
  }

  /// <summary>
  /// Generates the Damm check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> payload) {
    var interim = 0;
    foreach (var ch in payload) {
      if (ch is < '0' or > '9')
        throw new ArgumentException("Payload must contain decimal digits only.", nameof(payload));
      interim = Table[interim, ch - '0'];
    }
    return interim;
  }
}

/// <summary>ISBN-10 and ISBN-13 check digits.</summary>
public static class Isbn {
  /// <summary>
  /// Generates the Isbn-10 Check Digit for the supplied value.
  /// </summary>
  public static char GenerateIsbn10CheckDigit(ReadOnlySpan<char> firstNineDigits) {
    if (firstNineDigits.Length != 9)
      throw new ArgumentException("ISBN-10 payload must contain exactly 9 digits.", nameof(firstNineDigits));

    var sum = 0;
    for (var i = 0; i < 9; ++i) {
      var digit = DecimalDigit(firstNineDigits[i]);
      sum += digit * (10 - i);
    }

    var check = (11 - sum % 11) % 11;
    return check == 10 ? 'X' : (char)('0' + check);
  }

  /// <summary>
  /// Determines whether the supplied value has a valid Isbn-10.
  /// </summary>
  public static bool ValidateIsbn10(ReadOnlySpan<char> isbn) {
    if (isbn.Length != 10)
      return false;

    var sum = 0;
    for (var i = 0; i < 9; ++i) {
      if (!TryDecimalDigit(isbn[i], out var digit))
        return false;
      sum += digit * (10 - i);
    }

    var last = char.ToUpperInvariant(isbn[9]);
    var check = last == 'X' ? 10 : last is >= '0' and <= '9' ? last - '0' : -1;
    return check >= 0 && (sum + check) % 11 == 0;
  }

  /// <summary>
  /// Generates the Isbn-13 Check Digit for the supplied value.
  /// </summary>
  public static int GenerateIsbn13CheckDigit(ReadOnlySpan<char> firstTwelveDigits) =>
    WeightedMod10(firstTwelveDigits, 12);

  /// <summary>
  /// Determines whether the supplied value has a valid Isbn-13.
  /// </summary>
  public static bool ValidateIsbn13(ReadOnlySpan<char> isbn) =>
    isbn.Length == 13 && ValidateWeightedMod10(isbn);
}

/// <summary>EAN/UPC/GTIN modulo-10 check digits.</summary>
public static class Gtin {
  /// <summary>
  /// Generates the GTIN check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> payload) => WeightedMod10FromRight(payload);

  /// <summary>
  /// Determines whether the supplied value has a valid GTIN.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> value) =>
    value.Length >= 2 && ValidateWeightedMod10(value);

  /// <summary>
  /// Generates the Ean-8 for the supplied value.
  /// </summary>
  public static int GenerateEan8(ReadOnlySpan<char> sevenDigits) {
    if (sevenDigits.Length != 7)
      throw new ArgumentException("EAN-8 payload must contain 7 digits.", nameof(sevenDigits));
    return GenerateCheckDigit(sevenDigits);
  }

  /// <summary>
  /// Generates the Ean-13 for the supplied value.
  /// </summary>
  public static int GenerateEan13(ReadOnlySpan<char> twelveDigits) {
    if (twelveDigits.Length != 12)
      throw new ArgumentException("EAN-13 payload must contain 12 digits.", nameof(twelveDigits));
    return GenerateCheckDigit(twelveDigits);
  }

  /// <summary>
  /// Generates the Upc A for the supplied value.
  /// </summary>
  public static int GenerateUpcA(ReadOnlySpan<char> elevenDigits) {
    if (elevenDigits.Length != 11)
      throw new ArgumentException("UPC-A payload must contain 11 digits.", nameof(elevenDigits));
    return GenerateCheckDigit(elevenDigits);
  }
}

/// <summary>International Bank Account Number MOD-97 validation.</summary>
public static class Iban {
  /// <summary>
  /// Determines whether the supplied value has a valid IBAN.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> iban) {
    if (iban.Length is < 15 or > 64)
      return false;

    Span<char> compact = stackalloc char[64];
    var length = 0;
    foreach (var c in iban) {
      if (char.IsWhiteSpace(c))
        continue;
      compact[length++] = char.ToUpperInvariant(c);
    }

    if (length is < 15 or > 34)
      return false;

    var remainder = 0;
    for (var i = 4; i < length + 4; ++i) {
      var c = compact[i % length];
      if (c is >= '0' and <= '9') {
        remainder = (remainder * 10 + c - '0') % 97;
      } else if (c is >= 'A' and <= 'Z') {
        var value = c - 'A' + 10;
        remainder = (remainder * 10 + value / 10) % 97;
        remainder = (remainder * 10 + value % 10) % 97;
      } else {
        return false;
      }
    }
    return remainder == 1;
  }
}

/// <summary>IMEI check digit (Luhn).</summary>
public static class Imei {
  /// <summary>
  /// Generates the IMEI check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> fourteenDigits) {
    if (fourteenDigits.Length != 14)
      throw new ArgumentException("IMEI payload must contain 14 digits.", nameof(fourteenDigits));
    return Luhn.GenerateCheckDigit(fourteenDigits);
  }

  /// <summary>
  /// Determines whether the supplied value has a valid IMEI.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> imei) => imei.Length == 15 && Luhn.Validate(imei);
}

/// <summary>ICCID check digit (Luhn).</summary>
public static class Iccid {
  /// <summary>
  /// Generates the ICCID check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> payload) => Luhn.GenerateCheckDigit(payload);
  /// <summary>
  /// Determines whether the supplied value has a valid ICCID.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> iccid) => iccid.Length is >= 18 and <= 22 && Luhn.Validate(iccid);
}

/// <summary>ISSN modulo-11 check digit.</summary>
public static class Issn {
  /// <summary>
  /// Generates the ISSN check digit for the supplied value.
  /// </summary>
  public static char GenerateCheckDigit(ReadOnlySpan<char> firstSevenDigits) {
    if (firstSevenDigits.Length != 7)
      throw new ArgumentException("ISSN payload must contain 7 digits.", nameof(firstSevenDigits));

    var sum = 0;
    for (var i = 0; i < 7; ++i)
      sum += DecimalDigit(firstSevenDigits[i]) * (8 - i);

    var check = (11 - sum % 11) % 11;
    return check == 10 ? 'X' : (char)('0' + check);
  }

  /// <summary>
  /// Determines whether the supplied value has a valid ISSN.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> issn) {
    if (issn.Length != 8)
      return false;
    for (var i = 0; i < 7; ++i)
      if (!TryDecimalDigit(issn[i], out _))
        return false;
    var expected = GenerateCheckDigit(issn[..7]);
    return char.ToUpperInvariant(issn[7]) == expected;
  }
}

/// <summary>ISIN check digit (ISO 6166 letter expansion followed by Luhn).</summary>
public static class Isin {
  /// <summary>
  /// Determines whether the supplied value has a valid ISIN.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> isin) {
    if (isin.Length != 12)
      return false;

    Span<char> expanded = stackalloc char[24];
    var length = 0;
    foreach (var raw in isin) {
      var c = char.ToUpperInvariant(raw);
      if (c is >= '0' and <= '9') {
        expanded[length++] = c;
      } else if (c is >= 'A' and <= 'Z') {
        var value = c - 'A' + 10;
        expanded[length++] = (char)('0' + value / 10);
        expanded[length++] = (char)('0' + value % 10);
      } else {
        return false;
      }
    }
    return Luhn.Validate(expanded[..length]);
  }

  /// <summary>
  /// Generates the ISIN check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> elevenCharacters) {
    if (elevenCharacters.Length != 11)
      throw new ArgumentException("ISIN payload must contain 11 characters.", nameof(elevenCharacters));

    Span<char> expanded = stackalloc char[22];
    var length = 0;
    foreach (var raw in elevenCharacters) {
      var c = char.ToUpperInvariant(raw);
      if (c is >= '0' and <= '9') {
        expanded[length++] = c;
      } else if (c is >= 'A' and <= 'Z') {
        var value = c - 'A' + 10;
        expanded[length++] = (char)('0' + value / 10);
        expanded[length++] = (char)('0' + value % 10);
      } else {
        throw new ArgumentException("ISIN payload may contain only ASCII letters and digits.", nameof(elevenCharacters));
      }
    }
    return Luhn.GenerateCheckDigit(expanded[..length]);
  }
}

/// <summary>CUSIP check digit.</summary>
public static class Cusip {
  /// <summary>
  /// Generates the Cusip check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> firstEightCharacters) {
    if (firstEightCharacters.Length != 8)
      throw new ArgumentException("CUSIP payload must contain 8 characters.", nameof(firstEightCharacters));

    var sum = 0;
    for (var i = 0; i < 8; ++i) {
      var value = Value(firstEightCharacters[i]);
      if ((i & 1) != 0)
        value *= 2;
      sum += value / 10 + value % 10;
    }
    return (10 - sum % 10) % 10;
  }

  /// <summary>
  /// Determines whether the supplied value has a valid Cusip.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> cusip) {
    if (cusip.Length != 9 || cusip[8] is < '0' or > '9')
      return false;
    try {
      return GenerateCheckDigit(cusip[..8]) == cusip[8] - '0';
    } catch (ArgumentException) {
      return false;
    }
  }

  private static int Value(char raw) {
    var c = char.ToUpperInvariant(raw);
    if (c is >= '0' and <= '9')
      return c - '0';
    if (c is >= 'A' and <= 'Z')
      return c - 'A' + 10;
    return c switch {
      '*' => 36,
      '@' => 37,
      '#' => 38,
      _ => throw new ArgumentException($"Invalid CUSIP character '{raw}'.")
    };
  }
}

/// <summary>SEDOL check digit.</summary>
public static class Sedol {
  private static readonly int[] Weights = [1, 3, 1, 7, 3, 9];

  /// <summary>
  /// Generates the Sedol check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> firstSixCharacters) {
    if (firstSixCharacters.Length != 6)
      throw new ArgumentException("SEDOL payload must contain 6 characters.", nameof(firstSixCharacters));

    var sum = 0;
    for (var i = 0; i < 6; ++i)
      sum += Value(firstSixCharacters[i]) * Weights[i];
    return (10 - sum % 10) % 10;
  }

  /// <summary>
  /// Determines whether the supplied value has a valid Sedol.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> sedol) {
    if (sedol.Length != 7 || sedol[6] is < '0' or > '9')
      return false;
    try {
      return GenerateCheckDigit(sedol[..6]) == sedol[6] - '0';
    } catch (ArgumentException) {
      return false;
    }
  }

  private static int Value(char raw) {
    var c = char.ToUpperInvariant(raw);
    if (c is >= '0' and <= '9')
      return c - '0';
    if (c is >= 'A' and <= 'Z')
      return c - 'A' + 10;
    throw new ArgumentException($"Invalid SEDOL character '{raw}'.");
  }
}

/// <summary>Vehicle Identification Number check digit.</summary>
public static class Vin {
  private static readonly int[] Weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

  /// <summary>
  /// Generates the VIN check digit for the supplied value.
  /// </summary>
  public static char GenerateCheckDigit(ReadOnlySpan<char> vinWithoutReliableCheckDigit) {
    if (vinWithoutReliableCheckDigit.Length != 17)
      throw new ArgumentException("VIN must contain exactly 17 characters.", nameof(vinWithoutReliableCheckDigit));

    var sum = 0;
    for (var i = 0; i < 17; ++i)
      sum += Transliterate(vinWithoutReliableCheckDigit[i]) * Weights[i];

    var remainder = sum % 11;
    return remainder == 10 ? 'X' : (char)('0' + remainder);
  }

  /// <summary>
  /// Determines whether the supplied value has a valid VIN.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> vin) {
    if (vin.Length != 17)
      return false;
    try {
      return char.ToUpperInvariant(vin[8]) == GenerateCheckDigit(vin);
    } catch (ArgumentException) {
      return false;
    }
  }

  private static int Transliterate(char raw) {
    var c = char.ToUpperInvariant(raw);
    if (c is >= '0' and <= '9')
      return c - '0';

    return c switch {
      'A' or 'J' => 1,
      'B' or 'K' or 'S' => 2,
      'C' or 'L' or 'T' => 3,
      'D' or 'M' or 'U' => 4,
      'E' or 'N' or 'V' => 5,
      'F' or 'W' => 6,
      'G' or 'P' or 'X' => 7,
      'H' or 'Y' => 8,
      'R' or 'Z' => 9,
      _ => throw new ArgumentException($"Invalid VIN character '{raw}'.")
    };
  }
}

/// <summary>ABA routing transit number check digit.</summary>
public static class AbaRouting {
  /// <summary>
  /// Generates the Aba Routing check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> firstEightDigits) {
    if (firstEightDigits.Length != 8)
      throw new ArgumentException("ABA routing payload must contain 8 digits.", nameof(firstEightDigits));

    ReadOnlySpan<int> weights = [3, 7, 1, 3, 7, 1, 3, 7];
    var sum = 0;
    for (var i = 0; i < 8; ++i)
      sum += DecimalDigit(firstEightDigits[i]) * weights[i];
    return (10 - sum % 10) % 10;
  }

  /// <summary>
  /// Determines whether the supplied value has a valid Aba Routing.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> routingNumber) {
    if (routingNumber.Length != 9 || routingNumber[8] is < '0' or > '9')
      return false;
    return GenerateCheckDigit(routingNumber[..8]) == routingNumber[8] - '0';
  }
}

/// <summary>US National Provider Identifier check digit.</summary>
public static class Npi {
  private const string Prefix = "80840";

  /// <summary>
  /// Generates the NPI check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> firstNineDigits) {
    if (firstNineDigits.Length != 9)
      throw new ArgumentException("NPI payload must contain 9 digits.", nameof(firstNineDigits));

    Span<char> payload = stackalloc char[14];
    Prefix.AsSpan().CopyTo(payload);
    firstNineDigits.CopyTo(payload[Prefix.Length..]);
    return Luhn.GenerateCheckDigit(payload);
  }

  /// <summary>
  /// Determines whether the supplied value has a valid NPI.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> npi) {
    if (npi.Length != 10)
      return false;
    Span<char> full = stackalloc char[15];
    Prefix.AsSpan().CopyTo(full);
    npi.CopyTo(full[Prefix.Length..]);
    return Luhn.Validate(full);
  }
}

/// <summary>POSTNET and PLANET barcode check digit (sum of digits completed to a multiple of 10).</summary>
public static class PostalBarcode {
  /// <summary>
  /// Generates the Postal Barcode check digit for the supplied value.
  /// </summary>
  public static int GenerateCheckDigit(ReadOnlySpan<char> payload) {
    var sum = 0;
    foreach (var c in payload)
      sum += DecimalDigit(c);
    return (10 - sum % 10) % 10;
  }

  /// <summary>
  /// Determines whether the supplied value has a valid Postal Barcode.
  /// </summary>
  public static bool Validate(ReadOnlySpan<char> value) =>
    value.Length >= 2 && value[^1] is >= '0' and <= '9' && GenerateCheckDigit(value[..^1]) == value[^1] - '0';
}

/// <summary>Generic weighted modulo check-digit helper.</summary>
public static class ModuloCheckDigit {
  /// <summary>
  /// Generates the  for the supplied value.
  /// </summary>
  public static int Generate(ReadOnlySpan<char> payload, ReadOnlySpan<int> weights, int modulus, bool complement = true) {
    if (weights.IsEmpty)
      throw new ArgumentException("At least one weight is required.", nameof(weights));
    if (modulus <= 1)
      throw new ArgumentOutOfRangeException(nameof(modulus));

    var sum = 0;
    for (var i = 0; i < payload.Length; ++i)
      sum += DecimalDigit(payload[i]) * weights[i % weights.Length];

    var remainder = sum % modulus;
    return complement ? (modulus - remainder) % modulus : remainder;
  }
}

/// <summary>Constant-weight validation helper.</summary>
public static class ConstantWeight {
  /// <summary>
  /// Determines whether the supplied value has a valid Constant Weight.
  /// </summary>
  public static bool Validate(ReadOnlySpan<byte> data, int expectedOneBits) {
    if (expectedOneBits < 0)
      return false;
    var count = 0;
    foreach (var value in data)
      count += System.Numerics.BitOperations.PopCount((uint)value);
    return count == expectedOneBits;
  }
}

internal static class CheckDigitHelpers {
  public static int DecimalDigit(char c) =>
    c is >= '0' and <= '9' ? c - '0' : throw new ArgumentException($"Expected decimal digit, got '{c}'.");

  public static bool TryDecimalDigit(char c, out int value) {
    if (c is >= '0' and <= '9') {
      value = c - '0';
      return true;
    }
    value = 0;
    return false;
  }

  public static int WeightedMod10(ReadOnlySpan<char> payload, int expectedLength) {
    if (payload.Length != expectedLength)
      throw new ArgumentException($"Expected {expectedLength} digits.", nameof(payload));
    return WeightedMod10FromRight(payload);
  }

  public static int WeightedMod10FromRight(ReadOnlySpan<char> payload) {
    var sum = 0;
    var weight = 3;
    for (var i = payload.Length - 1; i >= 0; --i) {
      sum += DecimalDigit(payload[i]) * weight;
      weight = 4 - weight;
    }
    return (10 - sum % 10) % 10;
  }

  public static bool ValidateWeightedMod10(ReadOnlySpan<char> value) {
    if (value.Length < 2 || value[^1] is < '0' or > '9')
      return false;
    return WeightedMod10FromRight(value[..^1]) == value[^1] - '0';
  }
}
