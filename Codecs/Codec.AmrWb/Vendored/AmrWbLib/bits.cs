
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.IO;
using System.Reflection;

namespace AmrWbLib;

public partial class AmrWb
{
    /*--------------------------------------------------------------------------*
    *                         BITS.H                                           *
    *--------------------------------------------------------------------------*
    *       Number of bits for different modes                              *
    *--------------------------------------------------------------------------*/

    private const int NBBITS_7k = 132;                  /* 6.60k  */
    private const int NBBITS_9k = 177;                  /* 8.85k  */
    private const int NBBITS_12k = 253;                 /* 12.65k */
    private const int NBBITS_14k = 285;                 /* 14.25k */
    private const int NBBITS_16k = 317;                 /* 15.85k */
    private const int NBBITS_18k = 365;                 /* 18.25k */
    private const int NBBITS_20k = 397;                 /* 19.85k */
    private const int NBBITS_23k = 461;                 /* 23.05k */
    private const int NBBITS_24k = 477;                 /* 23.85k */

    private const int NBBITS_SID = 35;
    private const int NB_BITS_MAX = NBBITS_24k;

    private const short BIT_0 = -127;
    private const short BIT_1 = 127;
    private const short BIT_0_ITU = 0x007F;
    private const short BIT_1_ITU = 0x0081;

    private const int BITS_SIZE_MAX = (3 + NB_BITS_MAX);          /* serial size max */
    private const short TX_FRAME_TYPE = 0x6b21;
    private const short RX_FRAME_TYPE = 0x6b20;

    private static short[] nb_of_bits = new short[NUM_OF_MODES]
    {
        NBBITS_7k,
        NBBITS_9k,
        NBBITS_12k,
        NBBITS_14k,
        NBBITS_16k,
        NBBITS_18k,
        NBBITS_20k,
        NBBITS_23k,
        NBBITS_24k,
        NBBITS_SID
    };

    private class TX_State
    {
        public short sid_update_counter;
        public short sid_handover_debt;
        public short prev_ft;
    }

    private class RX_State
    {
        public short prev_ft;
        public short prev_mode;
    }

    /*-----------------------------------------------------*
     * Write_serial -> write serial stream into a file     *
     *-----------------------------------------------------*/

    private short Init_write_serial(TX_State st)
    {
        TX_State s = new TX_State();
        st = s;
        return 0;
    }

    private short Close_write_serial(TX_State st)
    {
        st = new TX_State();    // Create a new instance to close
        return 1;
    }

    private void Reset_write_serial(TX_State st)
    {
        st.sid_update_counter = 3;
        st.sid_handover_debt = 0;
        st.prev_ft = TX_SPEECH;
    }


//    void Write_serial(FILE* fp, short prms[], short coding_mode, short mode, TX_State* st, short bitstreamformat)
//    {
//        short i, frame_type;
//        short stream[SIZE_MAX];
//        byte temp;
//        byte* stream_ptr;

//        if (coding_mode == MRDTX)
//        {
//            st->sid_update_counter--;

//            if (st->prev_ft == TX_SPEECH)
//            {
//                frame_type = TX_SID_FIRST;
//                st->sid_update_counter = 3;
//            }
//            else
//            {
//                if ((st->sid_handover_debt > 0) &&
//                    (st->sid_update_counter > 2))
//                {
//                    /* ensure extra updates are  properly delayed after a possible SID_FIRST */
//                    frame_type = TX_SID_UPDATE;
//                    st->sid_handover_debt--;
//                }
//                else
//                {
//                    if (st->sid_update_counter == 0)
//                    {
//                        frame_type = TX_SID_UPDATE;
//                        st->sid_update_counter = 8;
//                    }
//                    else
//                    {
//                        frame_type = TX_NO_DATA;
//                    }
//                }
//            }
//        }
//        else
//        {
//            st->sid_update_counter = 8;
//            frame_type = TX_SPEECH;
//        }
//        st->prev_ft = frame_type;


//        if (bitstreamformat == 0)               /* default file format */
//        {
//            stream[0] = TX_FRAME_TYPE;
//            stream[1] = frame_type;
//            stream[2] = mode;
//            for (i = 0; i < nb_of_bits[coding_mode]; i++)
//            {
//                stream[3 + i] = prms[i];
//            }

//            fwrite(stream, sizeof(short), 3 + nb_of_bits[coding_mode], fp);

//        }
//        else
//        {
//            if (bitstreamformat == 1)       /* ITU file format */
//            {
//                stream[0] = 0x6b21;

//                if (frame_type != TX_NO_DATA && frame_type != TX_SID_FIRST)
//                {
//                    stream[1] = nb_of_bits[coding_mode];
//                    for (i = 0; i < nb_of_bits[coding_mode]; i++)
//                    {
//                        if (prms[i] == BIT_0)
//                        {
//                            stream[2 + i] = BIT_0_ITU;
//                        }
//                        else
//                        {
//                            stream[2 + i] = BIT_1_ITU;
//                        }
//                    }
//                    fwrite(stream, sizeof(short), 2 + nb_of_bits[coding_mode], fp);
//                }
//                else
//                {
//                    stream[1] = 0;
//                    fwrite(stream, sizeof(short), 2, fp);
//                }
//            }
//            else                            /* MIME/storage file format */
//            {
//#define MRSID 9
//                /* change mode index in case of SID frame */
//                if (coding_mode == MRDTX)
//                {
//                    coding_mode = MRSID;

//                    if (frame_type == TX_SID_FIRST)
//                    {
//                        for (i = 0; i < NBBITS_SID; i++) prms[i] = BIT_0;
//                    }
//                }

//                /* we cannot handle unspecified frame types (modes 10 - 13) */
//                /* -> force NO_DATA frame */
//                if (coding_mode < 0 || coding_mode > 15 || (coding_mode > MRSID && coding_mode < 14))
//                {
//                    coding_mode = 15;
//                }

//                /* mark empty frames between SID updates as NO_DATA frames */
//                if (coding_mode == MRSID && frame_type == TX_NO_DATA)
//                {
//                    coding_mode = 15;
//                }

//                /* set pointer for packed frame, note that we handle data as bytes */
//                stream_ptr = (byte*)stream;

//                /* insert table of contents (ToC) byte at the beginning of the packet */
//                *stream_ptr = toc_byte[coding_mode];
//                stream_ptr++;

//                temp = 0;

//                /* sort and pack AMR-WB speech or SID bits */
//                for (i = 1; i < unpacked_size[coding_mode] + 1; i++)
//                {
//                    if (prms[sort_ptr[coding_mode][i - 1]] == BIT_1)
//                    {
//                        temp++;
//                    }

//                    if (i % 8)
//                    {
//                        temp <<= 1;
//                    }
//                    else
//                    {
//                        *stream_ptr = temp;
//                        stream_ptr++;
//                        temp = 0;
//                    }
//                }

//                /* insert SID type indication and speech mode in case of SID frame */
//                if (coding_mode == MRSID)
//                {
//                    if (frame_type == TX_SID_UPDATE)
//                    {
//                        temp++;
//                    }
//                    temp <<= 4;

//                    temp += mode & 0x000F;
//                }

//                /* insert unused bits (zeros) at the tail of the last byte */
//                if (unused_size[coding_mode])
//                {
//                    temp <<= (unused_size[coding_mode] - 1);
//                }
//                *stream_ptr = temp;

//                /* write packed frame into file (1 byte added to cover ToC entry) */
//                fwrite(stream, sizeof(byte), 1 + packed_size[coding_mode], fp);
//            }
//        }
//        return;
//    }


    /*-----------------------------------------------------*
     * Read_serial -> read serial stream into a file       *
     *-----------------------------------------------------*/

    private short Init_read_serial(ref RX_State st)
    {
        RX_State s = new RX_State();
        Reset_read_serial(s);
        st = s;
        return 0;
    }

    private short Close_read_serial(ref RX_State st)
    {
        st = new RX_State();
        return 1;
    }

    private void Reset_read_serial(RX_State st)
    {
        st.prev_ft = RX_SPEECH_GOOD;
        st.prev_mode = 0;
    }

    private const int MRSID = 9;

    private unsafe int ReadSerialPacket(byte* pPacketBytes, int PacketBytesLength, short[] prms,
        short* frame_type, short* mode)
    {
        short coding_mode, i;
        byte toc, q, temp;
        byte* packet_ptr;

        byte PayloadHeader = pPacketBytes[0];
        toc = pPacketBytes[1];
        /* extract q and mode from ToC */
        q = (byte) ((toc >> 2) & 0x01);
        *mode = (byte) ((toc >> 3) & 0x0F);

        if ((PacketBytesLength - 2) != packed_size[*mode])
            return 0;     // This is an error condition

        packet_ptr = &pPacketBytes[2];
        temp = *packet_ptr;
        packet_ptr++;

        /* unpack and unsort speech or SID bits */
        for (i = 1; i < unpacked_size[*mode] + 1; i++)
        {
            if ((temp & 0x80) == 0x80) prms[sort_ptr[*mode][i - 1]] = BIT_1;
            else prms[sort_ptr[*mode][i - 1]] = BIT_0;

            if (i % 8 != 0)
            {
                temp <<= 1;
            }
            else
            {
                temp = *packet_ptr;
                packet_ptr++;
            }
        }

        /* set frame type */
        switch (*mode)
        {
            case MODE_7k:
            case MODE_9k:
            case MODE_12k:
            case MODE_14k:
            case MODE_16k:
            case MODE_18k:
            case MODE_20k:
            case MODE_23k:
            case MODE_24k:
                if (q == 1) *frame_type = RX_SPEECH_GOOD;
                else *frame_type = RX_SPEECH_BAD;
                break;
            case MRSID:
                if (q == 1)
                {
                    if ((temp & 0x80) == 0x80) *frame_type = RX_SID_UPDATE;
                    else *frame_type = RX_SID_FIRST;
                }
                else
                {
                    *frame_type = RX_SID_BAD;
                }

                /* read speech mode indication */
                coding_mode = (short) ((temp >> 3) & 0x0F);

                /* set mode index */
                *mode = m_RxState.prev_mode;
                break;
            case 14:        /* SPEECH_LOST */
                *frame_type = RX_SPEECH_LOST;
                *mode = m_RxState.prev_mode;
                break;
            case 15:        /* NO_DATA */
                *frame_type = RX_NO_DATA;
                *mode = m_RxState.prev_mode;
                break;
            default:        /* replace frame with unused mode index by NO_DATA frame */
                *frame_type = RX_NO_DATA;
                *mode = m_RxState.prev_mode;
                break;
        } // end switch

        m_RxState.prev_mode = *mode;

        /* return 1 to indicate succesfully parsed frame */
        return 1;
    }

    // 13 May 25 PHR
    private short[]? ReadSerialParams(BinaryReader reader, ref short frame_type, ref short mode, out string? error)
    {
        short[]? prms = null;
        short type_of_frame_type;
        error = null;
        int NumberOfParams = 0;
        try
        {
            type_of_frame_type = reader.ReadInt16();
            frame_type = reader.ReadInt16();
            mode = reader.ReadInt16();

            if (mode < 0 || mode > 8)
            {
                error = "Invalid mode read";
                return null;
            }

            if (type_of_frame_type == TX_FRAME_TYPE)
            {
                switch (frame_type)
                {
                    case TX_SPEECH:
                        frame_type = RX_SPEECH_GOOD;
                        break;
                    case TX_SID_FIRST:
                        frame_type = RX_SID_FIRST;
                        break;
                    case TX_SID_UPDATE:
                        frame_type = RX_SID_UPDATE;
                        break;
                    case TX_NO_DATA:
                        frame_type = RX_NO_DATA;
                        break;
                }
            }
            else if (type_of_frame_type != RX_FRAME_TYPE)
            {
                error = "Wrong type of frame type";
                return null;
            }

            NumberOfParams = nb_of_bits[mode];
            prms = new short[NumberOfParams];
            for (int i=0; i < NumberOfParams; i++)
            {
                prms[i] = reader.ReadInt16();
            }
        }
        catch (EndOfStreamException)
        {
            prms = null;
        }

        return prms;
    }

    //    short Read_serial(FILE* fp, short prms[], short* frame_type, short* mode, RX_State* st, short bitstreamformat)
    //    {
    //        short n, n1, type_of_frame_type, coding_mode, datalen, i;
    //        byte toc, q, temp, *packet_ptr, packet[64];

    //        if (bitstreamformat == 0)               /* default file format */
    //        {
    //            n = (short)fread(&type_of_frame_type, sizeof(short), 1, fp);
    //            n = (short)(n + fread(frame_type, sizeof(short), 1, fp));
    //            n = (short)(n + fread(mode, sizeof(short), 1, fp));
    //            coding_mode = *mode;
    //            if (*mode < 0 || *mode > NUM_OF_MODES - 1)
    //            {
    //                fprintf(stderr, "Invalid mode received: %d (check file format).\n", *mode);
    //                exit(-1);
    //            }
    //            if (n == 3)
    //            {
    //                if (type_of_frame_type == TX_FRAME_TYPE)
    //                {
    //                    switch (*frame_type)
    //                    {
    //                        case TX_SPEECH:
    //                            *frame_type = RX_SPEECH_GOOD;
    //                            break;
    //                        case TX_SID_FIRST:
    //                            *frame_type = RX_SID_FIRST;
    //                            break;
    //                        case TX_SID_UPDATE:
    //                            *frame_type = RX_SID_UPDATE;
    //                            break;
    //                        case TX_NO_DATA:
    //                            *frame_type = RX_NO_DATA;
    //                            break;
    //                    }
    //                }
    //                else if (type_of_frame_type != RX_FRAME_TYPE)
    //                {
    //                    fprintf(stderr, "Wrong type of frame type:%d.\n", type_of_frame_type);
    //                }

    //                if ((*frame_type == RX_SID_FIRST) | (*frame_type == RX_SID_UPDATE) | (*frame_type == RX_NO_DATA) | (*frame_type == RX_SID_BAD))
    //                {
    //                    coding_mode = MRDTX;
    //                }
    //                n = (short)fread(prms, sizeof(short), nb_of_bits[coding_mode], fp);
    //                if (n != nb_of_bits[coding_mode])
    //                    n = 0;
    //            }
    //            return (n);
    //        }
    //        else
    //        {
    //            if (bitstreamformat == 1)       /* ITU file format */
    //            {
    //                n = (short)fread(&type_of_frame_type, sizeof(short), 1, fp);
    //                n = (short)(n + fread(&datalen, sizeof(short), 1, fp));

    //                if (n == 2)
    //                {
    //                    if (type_of_frame_type == 0x6b20)        /* bad frame */
    //                    {
    //                        *frame_type = RX_SPEECH_LOST;
    //                        *mode = st->prev_mode;
    //                    }
    //                    else if (type_of_frame_type == 0x6b21)   /* good frame */
    //                    {
    //                        if (datalen == 0)                       /* RX_NO_DATA frame type */
    //                        {
    //                            if (st->prev_ft == RX_SPEECH_GOOD)
    //                            {
    //                                *frame_type = RX_SID_FIRST;
    //                            }
    //                            else
    //                            {
    //                                *frame_type = RX_NO_DATA;
    //                            }
    //                            *mode = st->prev_mode;
    //                        }
    //                        else
    //                        {
    //                            coding_mode = -1;
    //                            for (i = NUM_OF_MODES - 1; i >= 0; i--)
    //                            {
    //                                if (datalen == nb_of_bits[i])
    //                                {
    //                                    coding_mode = i;
    //                                }
    //                            }

    //                            if (coding_mode == -1)
    //                            {
    //                                fprintf(stderr, "\n\n ERROR: Invalid number of data bits received [%d]\n\n", datalen);
    //                                exit(-1);
    //                            }

    //                            if (coding_mode == NUM_OF_MODES - 1)    /* DTX frame type */
    //                            {
    //                                *frame_type = RX_SID_UPDATE;
    //                                *mode = st->prev_mode;
    //                            }
    //                            else
    //                            {
    //                                *frame_type = RX_SPEECH_GOOD;
    //                                *mode = coding_mode;
    //                            }
    //                        }
    //                        st->prev_mode = *mode;
    //                        st->prev_ft = *frame_type;
    //                    }
    //                    else
    //                    {
    //                        fprintf(stderr, "\n\n ERROR: Invalid ITU file format \n\n");
    //                        exit(-1);
    //                    }
    //                }
    //                n1 = fread(prms, sizeof(short), datalen, fp);
    //                n += n1;
    //                for (i = 0; i < n1; i++)
    //                {
    //                    if (prms[i] <= BIT_0_ITU) prms[i] = BIT_0;
    //                    else prms[i] = BIT_1;
    //                }
    //                return (n);

    //            }
    //            else                            /* MIME/storage file format */
    //            {
    //                /* read ToC byte, return immediately if no more data available */
    //                if (fread(&toc, sizeof(byte), 1, fp) == 0)
    //                {
    //                    return 0;
    //                }

    //                /* extract q and mode from ToC */
    //                q = (toc >> 2) & 0x01;
    //                *mode = (toc >> 3) & 0x0F;

    //                /* read speech bits, return with empty frame if mismatch between mode info and available data */
    //                if ((short)fread(packet, sizeof(byte), packed_size[*mode], fp) != packed_size[*mode])
    //                {
    //                    return 0;
    //                }

    //                packet_ptr = (byte*)packet;
    //                temp = *packet_ptr;
    //                packet_ptr++;

    //                /* unpack and unsort speech or SID bits */
    //                for (i = 1; i < unpacked_size[*mode] + 1; i++)
    //                {
    //                    if (temp & 0x80) prms[sort_ptr[*mode][i - 1]] = BIT_1;
    //                    else prms[sort_ptr[*mode][i - 1]] = BIT_0;

    //                    if (i % 8)
    //                    {
    //                        temp <<= 1;
    //                    }
    //                    else
    //                    {
    //                        temp = *packet_ptr;
    //                        packet_ptr++;
    //                    }
    //                }

    //                /* set frame type */
    //                switch (*mode)
    //                {
    //                    case MODE_7k:
    //                    case MODE_9k:
    //                    case MODE_12k:
    //                    case MODE_14k:
    //                    case MODE_16k:
    //                    case MODE_18k:
    //                    case MODE_20k:
    //                    case MODE_23k:
    //                    case MODE_24k:
    //                        if (q) *frame_type = RX_SPEECH_GOOD;
    //                        else *frame_type = RX_SPEECH_BAD;
    //                        break;
    //                    case MRSID:
    //                        if (q)
    //                        {
    //                            if (temp & 0x80) *frame_type = RX_SID_UPDATE;
    //                            else *frame_type = RX_SID_FIRST;
    //                        }
    //                        else
    //                        {
    //                            *frame_type = RX_SID_BAD;
    //                        }

    //                        /* read speech mode indication */
    //                        coding_mode = (temp >> 3) & 0x0F;

    //                        /* set mode index */
    //                        *mode = st->prev_mode;
    //                        break;
    //                    case 14:        /* SPEECH_LOST */
    //                        *frame_type = RX_SPEECH_LOST;
    //                        *mode = st->prev_mode;
    //                        break;
    //                    case 15:        /* NO_DATA */
    //                        *frame_type = RX_NO_DATA;
    //                        *mode = st->prev_mode;
    //                        break;
    //                    default:        /* replace frame with unused mode index by NO_DATA frame */
    //                        *frame_type = RX_NO_DATA;
    //                        *mode = st->prev_mode;
    //                        break;
    //                }

    //                st->prev_mode = *mode;

    //                /* return 1 to indicate succesfully parsed frame */
    //                return 1;
    //            }
    //#undef MRSID
    //        }

    //    }


    /*-----------------------------------------------------*
     * Parm_serial -> convert parameters to serial stream  *
     *-----------------------------------------------------*/

    //private void Parm_serial(
    //     short value,                         /* input : parameter value */
    //     short no_of_bits,                    /* input : number of bits  */
    //     //short** prms
    //     short[] prms
    //)
    //{
    //    short i, bit;

    //    prms += no_of_bits;

    //    for (i = 0; i < no_of_bits; i++)
    //    {
    //        bit = (short)(value & 0x0001); /* get lsb */

    //        if (bit == 0)
    //            *--(*prms) = BIT_0;
    //        else
    //            *--(*prms) = BIT_1;
    //        value = shr(value, 1); move16();
    //    }
    //    *prms += no_of_bits; move16();
    //    return;
    //}

    // 9 Jan 24 PHR
    private void Parm_serial(
         short value,                         /* input : parameter value */
         short no_of_bits,                    /* input : number of bits  */
         //short** prms
         short[] prms,
         ref int CurrentIndex   // 9 Jan 24 PHR -- Added this. Its updated in this function
    )
    {
        short i, bit;
        int TempCurrentIndex = CurrentIndex + no_of_bits;

        for (i = 0; i < no_of_bits; i++)
        {
            bit = (short)(value & 0x0001); /* get lsb */
            TempCurrentIndex -= 1;

            if (bit == 0)
                prms[TempCurrentIndex] = BIT_0;
            else
                prms[TempCurrentIndex] = BIT_1;

            value = shr(value, 1);
        }

        CurrentIndex += no_of_bits; ;
        return;
    }

    /*----------------------------------------------------*
     * Serial_parm -> convert serial stream to parameters *
     *----------------------------------------------------*/

    private short Serial_parm(                /* Return the parameter    */
         short no_of_bits,                    /* input : number of bits  */
         short[] prms,
         ref int CurrentIndex   // 9 Jan 24 PHR -- Added this. Its updated in this function
    )
    {
        short value, i;
        short bit;

        value = 0;
        for (i = 0; i < no_of_bits; i++)
        {
            value = shl(value, 1);
            bit = prms[CurrentIndex++];
            if (bit == BIT_1)
                value = add(value, 1);
        }

        return (value);
    }

    // 16 Jan 24 PHR
    private unsafe byte[] WriteSerialPacket(short[] prms, short coding_mode)
    {
        short i, frame_type;
        short* stream = stackalloc short[BITS_SIZE_MAX];
        byte temp;
        byte* stream_ptr;

        if (coding_mode == MRDTX)
        {
            m_EncoderTxState.sid_update_counter--;

            if (m_EncoderTxState.prev_ft == TX_SPEECH)
            {
                frame_type = TX_SID_FIRST;
                m_EncoderTxState.sid_update_counter = 3;
            }
            else
            {
                if ((m_EncoderTxState.sid_handover_debt > 0) &&
                    (m_EncoderTxState.sid_update_counter > 2))
                {
                    /* ensure extra updates are  properly delayed after a possible SID_FIRST */
                    frame_type = TX_SID_UPDATE;
                    m_EncoderTxState.sid_handover_debt--;
                }
                else
                {
                    if (m_EncoderTxState.sid_update_counter == 0)
                    {
                        frame_type = TX_SID_UPDATE;
                        m_EncoderTxState.sid_update_counter = 8;
                    }
                    else
                    {
                        frame_type = TX_NO_DATA;
                    }
                }
            }
        }
        else
        {
            m_EncoderTxState.sid_update_counter = 8;
            frame_type = TX_SPEECH;
        }
        m_EncoderTxState.prev_ft = frame_type;

        /* set pointer for packed frame, note that we handle data as bytes */
        stream_ptr = (byte*)stream;

        // 17 Jan 24 PHR
        // Insert the payload header as specified in Section 4.4.1 of RFC 4867 for octet aligned mode.
        *stream_ptr++ = (byte)(m_EncoderMode << 4);

        /* insert table of contents (ToC) byte at the beginning of the packet */
        *stream_ptr = toc_byte[coding_mode];
        stream_ptr++;

        temp = 0;

        /* sort and pack AMR-WB speech or SID bits */
        for (i = 1; i < unpacked_size[coding_mode] + 1; i++)
        {
            if (prms[sort_ptr[coding_mode][i - 1]] == BIT_1)
            {
                temp++;
            }

            if (i % 8 != 0)
            {
                temp <<= 1;
            }
            else
            {
                *stream_ptr = temp;
                stream_ptr++;
                temp = 0;
            }
        }

        /* insert unused bits (zeros) at the tail of the last byte */
        if (unused_size[coding_mode] != 0)
        {
            temp <<= (unused_size[coding_mode] - 1);
        }
        *stream_ptr = temp;

        /* write packed frame into file (1 byte added to cover ToC entry) */
        // int NumberOfPackedBytes = 1 + packed_size[coding_mode];

        // 17 Jan 24 PHR
        // 1 byte added for the payload header and 1 byte added for the TOC entry
        int NumberOfPackedBytes = 2 + packed_size[coding_mode];

        stream_ptr = (byte*)stream;
        byte[] PacketBytes = new byte[NumberOfPackedBytes];
        for (i = 0; i < NumberOfPackedBytes; i++)
            PacketBytes[i] = *stream_ptr++;

        //fwrite(stream, sizeof(byte), 1 + packed_size[coding_mode], fp);

        return PacketBytes;
    }


}
