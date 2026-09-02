/////////////////////////////////////////////////////////////////////////////////////
//  File:   PacketDecoder.cs                                        13 May 25 PHR
/////////////////////////////////////////////////////////////////////////////////////

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private Decoder_State m_DecoderState = new Decoder_State();
    private RX_State m_RxState = new RX_State();

    private void InitializeDecoder()
    {
        dtx_dec_init(m_DecoderState.dtx_decSt, isf_init);
        Reset_decoder(m_DecoderState, 1);
        Reset_read_serial(m_RxState);
    }

    // State variables used by DecodePacketPayload()
    private short reset_flag = 0;
    private short reset_flag_old = 1;
    private short mode_old = 0;

    /// <summary>
    /// Decodes the AMR-WB encoded bytes from the payload of a RTP packet into an array of linear 16-bit PCM
    /// samples with a sample rate of 16k samples/sec. Each RTP packet contains 20 millseconds of audio data.
    /// </summary>
    /// <param name="PacketPayloadBytes">Input payload from a RTP packet.</param>
    /// <returns>
    /// Returns an array of 16-bit audio samples. Returns null if an error occurred.
    /// </returns>
    /// <remarks>
    /// This function can only handle a single AMR-WB voice frame in the RTP packet payload bytes. If
    /// the application receives RTP packets containing multiple voice frames, then it must split the
    /// voice frame up and then send them one at a time to this function.
    /// </remarks>
    public unsafe short[]? DecodePacketPayload(byte[] PacketPayloadBytes)
    {
        short* synth = stackalloc short[L_FRAME16k];              /* Buffer for speech @ 16kHz             */
        short[] prms = new short[NB_BITS_MAX];
        short i;
        short mode = 0;
        short frame_type = 0;
        short frame_length = 0;

        byte* PacketBytes = stackalloc byte[L_FRAME16k];

        // Copy the input packet payload bytes into an unmanaged memory byte array
        for (i = 0; i<PacketPayloadBytes.Length; i++)
            PacketBytes[i] = PacketPayloadBytes[i];

        int Success = ReadSerialPacket(PacketBytes, PacketPayloadBytes.Length, prms, &frame_type, &mode);
        if (Success == 0)
            return null;

        short[] Samples = new short[L_FRAME16k];
        if ((frame_type == RX_NO_DATA) || (frame_type == RX_SPEECH_LOST))
        {
            mode = mode_old;
            reset_flag = 0;
        }
        else
        {
            mode_old = mode;

            /* if homed: check if this frame is another homing frame */
            if (reset_flag_old == 1)
            {
                /* only check until end of first subframe */
                reset_flag = decoder_homing_frame_test_first(prms, mode);
            }
        }

        /* produce encoder homing frame if homed & input=decoder homing frame */
        if ((reset_flag != 0) && (reset_flag_old != 0))
        {
            for (i = 0; i < L_FRAME16k; i++)
            {
                synth[i] = EHF_MASK;
            }
        }
        else
        {
            decoder(mode, prms, synth, &frame_length, m_DecoderState, frame_type);
        }

        // Copy the results into a managed array.
        for (i = 0; i < L_FRAME16k; i++)
            Samples[i] = (short) (synth[i] & 0xfffc);   // Delete the 2 LSBs (14-bit output)

        /* if not homed: check whether current frame is a homing frame */
        if (reset_flag_old == 0)
        {
            /* check whole frame */
            reset_flag = decoder_homing_frame_test(prms, mode);
        }
        /* reset decoder if current frame is a homing frame */
        if (reset_flag != 0)
        {
            Reset_decoder(m_DecoderState, 1);
        }
        reset_flag_old = reset_flag;

        return Samples;
    }

    /// <summary>
    /// Reads a *.cod file containing AMR-WB encoded audio data, decodes it and returns a list of audio samples.
    /// <para>
    /// A *.cod file is a binary file containing AMR-WB encoded binary parameter data. The file format is the default
    /// file format for the decoder test files provided by the 3GPP organization for the AMR-WB codec.
    /// </para>
    /// <para>This function is for testing only.</para>
    /// </summary>
    /// <param name="InputCodFile">Input *.cod file containing AMR-WB encoded audio data.</param>
    /// <returns>Returns a list of audio samples. The format is 16-bit linear PCM. The list will be empty if the input *.cod 
    /// file does not exist.</returns>
    public unsafe List<short> DecodeCodFile(string InputCodFile)
    {
        List<short> SamplesList = new List<short>();
        if (File.Exists(InputCodFile) == false)
            return SamplesList;

        byte[] FileBytes = File.ReadAllBytes(InputCodFile);
        MemoryStream inputStream = new MemoryStream(FileBytes);
        BinaryReader reader = new BinaryReader(inputStream);

        bool Done = false;
        short[]? prms = null;
        
        short frameType = 0;
        string? error = null;
        short* synth = stackalloc short[L_FRAME16k];

        short mode = 0;
        int i;
        short FrameLength = L_FRAME16k;
        int frame = 0;

        while (Done == false)
        {
            prms = ReadSerialParams(reader, ref frameType, ref mode, out error);
            if (prms != null)
            {
                frame += 1;
                if ((frameType == RX_NO_DATA) || (frameType == RX_SPEECH_LOST))
                {
                    mode = mode_old;
                    reset_flag = 0;
                }
                else
                {
                    mode_old = mode;

                    /* if homed: check if this frame is another homing frame */
                    if (reset_flag_old == 1)
                    {
                        /* only check until end of first subframe */
                        reset_flag = decoder_homing_frame_test_first(prms, mode);
                    }
                }

                /* produce encoder homing frame if homed & input=decoder homing frame */
                if ((reset_flag != 0) && (reset_flag_old != 0))
                {
                    for (i = 0; i < L_FRAME16k; i++)
                    {
                        synth[i] = EHF_MASK;
                    }
                }
                else
                {
                    decoder(mode, prms, synth, &FrameLength, m_DecoderState, frameType);
                }

                short[] Samples = new short[L_FRAME16k];
                for (i = 0; i < L_FRAME16k; i++)
                {
                    Samples[i] = (short) (synth[i] & 0xfffc);   // Delete the 2 LSBs (14-bit output)
                }

                SamplesList.AddRange(Samples);

                /* if not homed: check whether current frame is a homing frame */
                if (reset_flag_old == 0)
                {
                    /* check whole frame */
                    reset_flag = decoder_homing_frame_test(prms, mode);
                }
                /* reset decoder if current frame is a homing frame */
                if (reset_flag != 0)
                {
                    Reset_decoder(m_DecoderState, 1);
                }
                reset_flag_old = reset_flag;
            }
            else
            {
                Done = true;
            }
        } // end while

        reader.Dispose();
        inputStream.Dispose();

        return SamplesList;
    }
}
