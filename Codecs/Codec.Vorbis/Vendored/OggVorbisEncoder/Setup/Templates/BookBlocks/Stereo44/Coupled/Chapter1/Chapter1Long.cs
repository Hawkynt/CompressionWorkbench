namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter1;

/// <summary>
/// Represents a chapter 1 long.
/// </summary>
public class Chapter1Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         5, 5, 9,10, 9, 9,10,11,12, 5, 1, 5, 6, 6, 7,10,
        12,14, 9, 5, 6, 8, 8,10,12,14,14,10, 5, 8, 5, 6,
         8,11,13,14, 9, 5, 7, 6, 6, 8,10,12,11, 9, 7, 9,
         7, 6, 6, 7,10,10,10, 9,12, 9, 8, 7, 7,10,12,11,
        11,13,12,10, 9, 8, 9,11,11,14,15,15,13,11, 9, 9,
        11,
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
