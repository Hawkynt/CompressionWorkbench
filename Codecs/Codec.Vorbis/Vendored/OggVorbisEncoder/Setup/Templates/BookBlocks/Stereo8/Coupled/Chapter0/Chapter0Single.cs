namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo8.Coupled.Chapter0;

/// <summary>
/// Represents a chapter 0 single.
/// </summary>
public class Chapter0Single : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         4, 5,18, 7,10, 6, 7, 8, 9,10, 5, 2,18, 5, 7, 5,
         6, 7, 8,11,17,17,17,17,17,17,17,17,17,17, 7, 4,
        17, 6, 9, 6, 8,10,12,15,11, 7,17, 9, 6, 6, 7, 9,
        11,15, 6, 4,17, 6, 6, 4, 5, 8,11,16, 6, 6,17, 8,
         6, 5, 6, 9,13,16, 8, 9,17,11, 9, 8, 8,11,13,17,
         9,12,17,15,14,13,12,13,14,17,12,15,17,17,17,17,
        17,16,17,17,
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
