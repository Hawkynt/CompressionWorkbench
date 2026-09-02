namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter5;

/// <summary>
/// Represents a chapter 5 short.
/// </summary>
public class Chapter5Short : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         4,10,17,13,17,13,17,17,17,17, 3, 6, 8, 9,11, 9,
        15,12,16,17, 6, 5, 5, 7, 7, 8,10,11,17,17, 7, 8,
         7, 9, 9,10,13,13,17,17, 8, 6, 5, 7, 4, 7, 5, 8,
        14,17, 9, 9, 8, 9, 7, 9, 8,10,16,17,12,10, 7, 8,
         4, 7, 4, 7,16,17,12,11, 9,10, 6, 9, 5, 7,14,17,
        14,13,10,15, 4, 8, 3, 5,14,17,17,14,11,15, 6,10,
         6, 8,15,17,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = null;
}
