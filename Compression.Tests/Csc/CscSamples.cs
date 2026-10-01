using System.Text;

namespace Compression.Tests.Csc;

/// <summary>
/// Deterministic inputs that steer libcsc's analyzer into each of its coders. The checked-in
/// reference vectors were produced from exactly these bytes (see ReferenceVectors/README.md), so the
/// generators must never change.
/// </summary>
public static class CscSamples {
  private static readonly string[] _vocabulary = [
    "the", "and", "that", "with", "have", "this", "from", "were", "said", "there", "which", "their",
    "about", "would", "these", "other", "words", "could", "write", "first", "water", "after", "where",
    "right", "think", "three", "years", "place", "sound", "great", "again", "still", "every", "small",
    "found", "those", "never", "under", "might", "while", "house", "world", "below", "asked", "going",
    "large", "until", "along", "shall", "being", "often", "earth", "began", "since", "study", "night",
    "light", "above", "paper", "parts", "young", "story", "point", "times", "heard", "whole", "white",
    "given", "means", "music", "miles", "thing", "today", "later", "using", "money", "lines", "order",
    "group", "among", "learn", "known", "space", "table", "early", "trees", "short", "hands", "state",
    "black", "shown", "stood", "front", "voice", "kinds", "makes", "comes", "close", "power", "lived",
    "vowel", "taken", "built", "heart", "ready", "quite", "class", "bring", "round", "horse", "shows",
    "piece", "green", "stand", "birds", "start", "river", "tried", "least", "field", "whose", "girls",
    "leave", "added", "color", "third", "hours", "moved", "plant", "doing", "names", "forms", "heavy",
  ];

  /// <summary>Generated English prose: lower-case words, spaces, punctuation and line breaks.</summary>
  public static byte[] Text(int length, int seed = 1) {
    var random = new Random(seed);
    var builder = new StringBuilder(length + 16);
    var wordsInLine = 0;
    while (builder.Length < length) {
      builder.Append(_vocabulary[random.Next(_vocabulary.Length)]);
      ++wordsInLine;
      if (random.Next(12) == 0)
        builder.Append(random.Next(3) == 0 ? ". " : ", ");
      else if (wordsInLine > 10 && random.Next(4) == 0) {
        builder.Append('\n');
        wordsInLine = 0;
      } else
        builder.Append(' ');
    }

    return Encoding.ASCII.GetBytes(builder.ToString(0, length));
  }

  /// <summary>x86-like machine code: register moves (0x8B/0x89 with small displacements) and near calls (0xE8 rel32).</summary>
  public static byte[] X86(int length, int seed = 2) {
    var random = new Random(seed);
    var data = new byte[length];
    var targets = new int[24];
    for (var i = 0; i < targets.Length; ++i)
      targets[i] = random.Next(length);

    for (var pos = 0; pos + 8 <= length;) {
      switch (random.Next(6)) {
        case 0: {
          // call rel32 to one of a few functions, so the absolute targets repeat after the E8 transform
          var relative = targets[random.Next(targets.Length)] - (pos + 5);
          data[pos++] = 0xE8;
          BitConverter.TryWriteBytes(data.AsSpan(pos), relative);
          pos += 4;
          break;
        }
        case 1:
          data[pos++] = 0x8B;
          data[pos++] = 0x45;
          data[pos++] = (byte)(random.Next(16) * 4);
          break;
        case 2:
          data[pos++] = 0x89;
          data[pos++] = 0x45;
          data[pos++] = (byte)(random.Next(16) * 4);
          break;
        case 3:
          data[pos++] = 0x8B;
          data[pos++] = 0x00;
          break;
        case 4:
          data[pos++] = 0x00;
          data[pos++] = 0x00;
          break;
        default:
          data[pos++] = (byte)(0x50 + random.Next(8));
          break;
      }
    }

    return data;
  }

  /// <summary>A table of 32-bit little-endian records whose fields change slowly — delta-coder material.</summary>
  public static byte[] Table(int length) {
    var data = new byte[length];
    for (var i = 0; i + 4 <= length; i += 4) {
      var record = i / 4;
      data[i] = (byte)(record * 3);
      data[i + 1] = (byte)(record / 7);
      data[i + 2] = (byte)(0x40 + record % 5);
      data[i + 3] = (byte)(record / 300);
    }

    return data;
  }

  /// <summary>Random text over eight symbols: incompressible by LZ, but well below 8 bits per byte.</summary>
  public static byte[] SmallAlphabet(int length, int seed = 3) {
    var random = new Random(seed);
    var data = new byte[length];
    for (var i = 0; i < length; ++i)
      data[i] = (byte)"ACGTacgt"[random.Next(8)];
    return data;
  }

  /// <summary>Uniform random bytes — stored uncompressed.</summary>
  public static byte[] RandomBytes(int length, int seed = 4) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  /// <summary>Text, random bytes, a table, a small alphabet and code back to back: one segment, five block types.</summary>
  public static byte[] Mixed() => [
    .. Text(16384, 6),
    .. RandomBytes(8192, 7),
    .. Table(32768),
    .. SmallAlphabet(8192, 8),
    .. X86(16384, 9),
  ];

  /// <summary>A 251-byte ramp repeated; long enough inputs span several 2 MiB segments.</summary>
  public static byte[] Ramp(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i)
      data[i] = (byte)(i % 251);
    return data;
  }
}
