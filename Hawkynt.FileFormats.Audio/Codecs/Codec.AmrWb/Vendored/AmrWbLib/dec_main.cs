
namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private class Decoder_State
    {
        public short[] old_exc = new short[PIT_MAX + L_INTERPOL];   /* old excitation vector */
        public short[] ispold = new short[M];                       /* old isp (immittance spectral pairs) */
        public short[] isfold = new short[M];                       /* old isf (frequency domain) */
        public short[] isf_buf = new short[L_MEANBUF * M];          /* isf buffer(frequency domain) */
        public short[] past_isfq = new short[M];                    /* past isf quantizer */
        public short tilt_code;                                     /* tilt of code */
        public short Q_old;                                         /* old scaling factor */
        public short[] Qsubfr = new short[4];                       /* old maximum scaling factor */
        public int L_gc_thres;                                      /* threshold for noise enhancer */
        public short[] mem_syn_hi = new short[M];                   /* modified synthesis memory (MSB) */
        public short[] mem_syn_lo = new short[M];                   /* modified synthesis memory (LSB) */
        public short mem_deemph;                                    /* speech deemph filter memory */
        public short[] mem_sig_out = new short[6];                  /* hp50 filter memory for synthesis */
        public short[] mem_oversamp= new short[2 * L_FILT];         /* synthesis oversampled filter memory */
        public short[] mem_syn_hf = new short[M16k];                /* HF synthesis memory */
        public short[] mem_hf = new short[2 * L_FILT16k];           /* HF band-pass filter memory */
        public short[] mem_hf2 = new short[2 * L_FILT16k];          /* HF band-pass filter memory */
        public short[] mem_hf3 = new short[2 * L_FILT16k];          /* HF band-pass filter memory */
        public short seed;                                          /* random memory for frame erasure */
        public short seed2;                                         /* random memory for HF generation */
        public short old_T0;                                        /* old pitch lag */
        public short old_T0_frac;                                   /* old pitch fraction lag */
        public short[] lag_hist = new short[5];
        public short[] dec_gain = new short[23];                    /* gain decoder memory */
        public short seed3;                                         /* random memory for lag concealment */
        public short[] disp_mem = new short[8];                     /* phase dispersion memory */
        public short[] mem_hp400 = new short[6];                    /* hp400 filter memory for synthesis */

        public short prev_bfi;
        public short state;
        public short first_frame;
        public dtx_decState dtx_decSt = new dtx_decState();
        public short vad_hist;

        public Decoder_State() { }
    }

    /*-----------------------------------------------------------------*
     *   Funtion  init_decoder                                         *
     *            ~~~~~~~~~~~~                                         *
     *   ->Initialization of variables for the decoder section.        *
     *-----------------------------------------------------------------*/

    private void Init_decoder(Decoder_State st)
    {
        dtx_dec_init(st.dtx_decSt, isf_init);
        Reset_decoder(st, 1);
    }

    private void Reset_decoder(Decoder_State dec_state, short reset_all)
    {
        short i;

        Set_zero(dec_state.old_exc, PIT_MAX + L_INTERPOL);
        Set_zero(dec_state.past_isfq, M);

        dec_state.old_T0_frac = 0;  /* old pitch value = 64.0 */
        dec_state.old_T0 = 64;
        dec_state.first_frame = 1;
        dec_state.L_gc_thres = 0;
        dec_state.tilt_code = 0;

        Init_Phase_dispersion(dec_state.disp_mem);

        /* scaling memories for excitation */
        dec_state.Q_old = Q_MAX;
        dec_state.Qsubfr[3] = Q_MAX;
        dec_state.Qsubfr[2] = Q_MAX;
        dec_state.Qsubfr[1] = Q_MAX;
        dec_state.Qsubfr[0] = Q_MAX;

        if (reset_all != 0)
        {
            /* routines initialization */

            Init_D_gain2(dec_state.dec_gain);
            Init_Oversamp_16k(dec_state.mem_oversamp);
            Init_HP50_12k8(dec_state.mem_sig_out);
            Init_Filt_6k_7k(dec_state.mem_hf);
            Init_Filt_7k(dec_state.mem_hf3);
            Init_HP400_12k8(dec_state.mem_hp400);
            Init_Lagconc(dec_state.lag_hist);

            /* isp initialization */

            Copy(isp_init, dec_state.ispold, M);
            Copy(isf_init, dec_state.isfold, M);

            //for (i = 0; i < L_MEANBUF; i++)
            //    Copy(isf_init, &dec_state->isf_buf[i * M], M);
            // 12 May 25 PHR
            for (i=0; i < L_MEANBUF; i++)
            {
                for (int j=0; j < M; j++)
                    dec_state.isf_buf[i * M + j] = isf_init[j];
            }

            /* variable initialization */

            dec_state.mem_deemph = 0;

            dec_state.seed = 21845;  /* init random with 21845 */
            dec_state.seed2 = 21845;
            dec_state.seed3 = 21845;

            dec_state.state = 0;
            dec_state.prev_bfi = 0;

            /* Static vectors to zero */

            Set_zero(dec_state.mem_syn_hf, M16k);
            Set_zero(dec_state.mem_syn_hi, M);
            Set_zero(dec_state.mem_syn_lo, M);

            dtx_dec_reset(ref dec_state.dtx_decSt, isf_init);
            dec_state.vad_hist = 0;
        }
    }

    private void Close_decoder(Decoder_State spd_state)
    {
        //dtx_dec_exit(&(((Decoder_State*)spd_state)->dtx_decSt));
        dtx_dec_exit(ref spd_state.dtx_decSt);
    }

    /*-----------------------------------------------------------------*
     *   Funtion decoder                                           *
     *           ~~~~~~~                                             *
     *   ->Main decoder routine.                                       *
     *                                                                 *
     *-----------------------------------------------------------------*/

    private unsafe void decoder(
         short mode,                    /* input : used mode                     */
         short[] prms,                  /* input : parameter vector              */
         short* synth16k,               /* output: dec_synthesis speech              */
         short* frame_length,           /* output:  lenght of the frame          */
         Decoder_State st,              /* i/o   : State structure               */
         short frame_type)              /* input : received frame type           */
    {

        /* Excitation vector */
        short* old_exc = stackalloc short[(L_FRAME + 1) + PIT_MAX + L_INTERPOL];
        short* exc;

        /* LPC coefficients */

        short* p_Aq;                                        /* ptr to A(z) for the 4 subframes      */
        short* Aq = stackalloc short[NB_SUBFR * (M + 1)];   /* A(z)   quantized for the 4 subframes */
        short[] ispnew = new short[M];                      /* immittance spectral pairs at 4nd sfr */
        short[] isf = new short[M];                         /* ISF (frequency domain) at 4nd sfr    */
        short* code = stackalloc short[L_SUBFR];            /* algebraic codevector                 */
        short[] code2 = new short[L_SUBFR];                 /* algebraic codevector                 */
        short* exc2 = stackalloc short[L_FRAME];            /* excitation vector                    */

        short fac, stab_fac, voice_fac, Q_new = 0;
        int L_tmp, L_gain_code;

        /* Scalars */

        short i, j, i_subfr, index;
        short[] ind = new short[8];
        short max, tmp;
        short T0, T0_frac, pit_flag, T0_max, select, T0_min = 0;
        short gain_pit, gain_code, gain_code_lo;
        short newDTXState, bfi, unusable_frame, nb_bits;
        short vad_flag;
        short pit_sharp;
        short[] excp = new short[L_SUBFR];
        short[] isf_tmp = new short[M];
        short[] HfIsf = new short[M16k];

        short corr_gain = 0;

        // 16 May 25 PHR -- For debug only
        Span<short> synth16k_Span = new Span<short>(synth16k, 320);
        Span<short> old_exc_Span = new Span<short>(old_exc, (L_FRAME + 1) + PIT_MAX + L_INTERPOL);
        Span<short> Aq_Span = new Span<short>(Aq, NB_SUBFR * (M + 1));
        Span<short> code_Span = new Span<short>(code, L_SUBFR);
        Span<short> exc2_Span = new Span<short>(exc2, L_FRAME);

        /* mode verification */

        nb_bits = nb_of_bits[mode];

        *frame_length = L_FRAME16k;

        // Index into the prms array
        int CurrentPrmsIndex = 0;

        /* find the new  DTX state  SPEECH OR DTX */
        newDTXState = rx_dtx_handler(st.dtx_decSt, frame_type);

        if (sub(newDTXState, SPEECH) != 0)
        {
            dtx_dec(st.dtx_decSt, exc2, newDTXState, isf, prms, ref CurrentPrmsIndex);
        }
        /* SPEECH action state machine  */
        if ((sub(frame_type, RX_SPEECH_BAD) == 0) ||
            (sub(frame_type, RX_SPEECH_PROBABLY_DEGRADED) == 0))
        {
            /* bfi only for lsf, gains and pitch period */
            bfi = 1;
            unusable_frame = 0;
        }
        else if ((sub(frame_type, RX_NO_DATA) == 0) ||
                   (sub(frame_type, RX_SPEECH_LOST) == 0))
        {
            /* bfi for all index, bits are not usable */
            bfi = 1;
            unusable_frame = 1;
        }
        else
        {
            bfi = 0;
            unusable_frame = 0;
        }
        
        if (bfi != 0)
        {
            st.state = add(st.state, 1);
            if (sub(st.state, 6) > 0)
            {
                st.state = 6;
            }
        }
        else
        {
            st.state = shr(st.state, 1);
        }

        /* If this frame is the first speech frame after CNI period,     */
        /* set the BFH state machine to an appropriate state depending   */
        /* on whether there was DTX muting before start of speech or not */
        /* If there was DTX muting, the first speech frame is muted.     */
        /* If there was no DTX muting, the first speech frame is not     */
        /* muted. The BFH state machine starts from state 5, however, to */
        /* keep the audible noise resulting from a SID frame which is    */
        /* erroneously interpreted as a good speech frame as small as    */
        /* possible (the decoder output in this case is quickly muted)   */
        
        if (sub(st.dtx_decSt.dtxGlobalState, DTX) == 0)
        {
            st.state = 5;
            st.prev_bfi = 0;
        }
        else if (sub(st.dtx_decSt.dtxGlobalState, DTX_MUTE) == 0)
        {
            st.state = 5;
            st.prev_bfi = 1;
        }
        
        if (sub(newDTXState, SPEECH) == 0)
        {
            vad_flag = Serial_parm(1, prms, ref CurrentPrmsIndex);
            
            if (bfi == 0)
            {
                if (vad_flag == 0)
                {
                    st.vad_hist = add(st.vad_hist, 1);
                    st.dtx_decSt.dtx_vad_hist = add(st.dtx_decSt.dtx_vad_hist, 1);
                }
                else
                {
                    st.vad_hist = 0;
                    st.dtx_decSt.dtx_vad_hist = 0;
                }
            }
            else if (st.dtx_decSt.dtx_vad_hist > 0)
            {
                st.dtx_decSt.dtx_vad_hist = add(st.dtx_decSt.dtx_vad_hist, 1);
            }
        }
        /*----------------------------------------------------------------------*
         *                              DTX-CNG                                 *
         *----------------------------------------------------------------------*/

        if (sub(newDTXState, SPEECH) != 0)     /* CNG mode */
        {
            /* increase slightly energy of noise below 200 Hz */

            /* Convert ISFs to the cosine domain */
            Isf_isp(isf, ispnew, M);

            Isp_Az(ispnew, Aq, M, 1);

            Copy(st.isfold, isf_tmp, M);

            for (i_subfr = 0; i_subfr < L_FRAME; i_subfr += L_SUBFR)
            {
                j = shr(i_subfr, 6);
                for (i = 0; i < M; i++)
                {
                    L_tmp = L_mult(isf_tmp[i], sub(32767, interpol_frac[j]));
                    L_tmp = L_mac(L_tmp, isf[i], interpol_frac[j]);
                    HfIsf[i] = round(L_tmp);
                }
                dec_synthesis(Aq, &exc2[i_subfr], 0, &synth16k[(i_subfr) * 5 / 4], (short)1, HfIsf, nb_bits, newDTXState, st, bfi);
            }

            /* reset speech coder memories */
            Reset_decoder(st, 0);

            Copy(isf, st.isfold, M);

            st.prev_bfi = bfi;
            st.dtx_decSt.dtxGlobalState = newDTXState;

            return;
        }
        /*----------------------------------------------------------------------*
         *                               ACELP                                  *
         *----------------------------------------------------------------------*/

        /* copy coder memory state into working space (internal memory for DSP) */

        Copy(st.old_exc, old_exc, PIT_MAX + L_INTERPOL);
        exc = old_exc + PIT_MAX + L_INTERPOL;

        /* Decode the ISFs */
        
        if (sub(nb_bits, NBBITS_7k) <= 0)
        {
            ind[0] = Serial_parm(8, prms, ref CurrentPrmsIndex);
            ind[1] = Serial_parm(8, prms, ref CurrentPrmsIndex);
            ind[2] = Serial_parm(7, prms, ref CurrentPrmsIndex);
            ind[3] = Serial_parm(7, prms, ref CurrentPrmsIndex);
            ind[4] = Serial_parm(6, prms, ref CurrentPrmsIndex);

            Dpisf_2s_36b(ind, isf, st.past_isfq, st.isfold, st.isf_buf, bfi, 1);
        }
        else
        {
            ind[0] = Serial_parm(8, prms, ref CurrentPrmsIndex);
            ind[1] = Serial_parm(8, prms, ref CurrentPrmsIndex);
            ind[2] = Serial_parm(6, prms, ref CurrentPrmsIndex);
            ind[3] = Serial_parm(7, prms, ref CurrentPrmsIndex);
            ind[4] = Serial_parm(7, prms, ref CurrentPrmsIndex);
            ind[5] = Serial_parm(5, prms, ref CurrentPrmsIndex);
            ind[6] = Serial_parm(5, prms, ref CurrentPrmsIndex);

            Dpisf_2s_46b(ind, isf, st.past_isfq, st.isfold, st.isf_buf, bfi, 1);
        }

        /* Convert ISFs to the cosine domain */

        Isf_isp(isf, ispnew, M);
        
        if (st.first_frame != 0)
        {
            st.first_frame = 0;
            Copy(ispnew, st.ispold, M);
        }
        /* Find the interpolated ISPs and convert to a[] for all subframes */
        Int_isp(st.ispold, ispnew, interpol_frac, Aq);

        /* update ispold[] for the next frame */
        Copy(ispnew, st.ispold, M);

        /* Check stability on isf : distance between old isf and current isf */

        L_tmp = 0;
        for (i = 0; i < M - 1; i++)
        {
            tmp = sub(isf[i], st.isfold[i]);
            L_tmp = L_mac(L_tmp, tmp, tmp);
        }
        tmp = extract_h(L_shl(L_tmp, 8));
        tmp = mult(tmp, 26214);                /* tmp = L_tmp*0.8/256 */

        tmp = sub(20480, tmp);                 /* 1.25 - tmp */
        stab_fac = shl(tmp, 1);                /* Q14 -> Q15 with saturation */
        
        if (stab_fac < 0)
        {
            stab_fac = 0;
        }
        Copy(st.isfold, isf_tmp, M);
        Copy(isf, st.isfold, M);

        /*------------------------------------------------------------------------*
         *          Loop for every subframe in the analysis frame                 *
         *------------------------------------------------------------------------*
         * The subframe size is L_SUBFR and the loop is repeated L_FRAME/L_SUBFR  *
         *  times                                                                 *
         *     - decode the pitch delay and filter mode                           *
         *     - decode algebraic code                                            *
         *     - decode pitch and codebook gains                                  *
         *     - find voicing factor and tilt of code for next subframe.          *
         *     - find the excitation and compute dec_synthesis speech                 *
         *------------------------------------------------------------------------*/

        p_Aq = Aq;  /* pointer to interpolated LPC parameters */

        for (i_subfr = 0; i_subfr < L_FRAME; i_subfr += L_SUBFR)
        {
            pit_flag = i_subfr;

            if ((sub(i_subfr, 2 * L_SUBFR) == 0) && (sub(nb_bits, NBBITS_7k) > 0))
            {
                pit_flag = 0;
            }
            /*-------------------------------------------------*
             * - Decode pitch lag                              *
             * Lag indeces received also in case of BFI,       *
             * so that the parameter pointer stays in sync.    *
             *-------------------------------------------------*/
            
            if (pit_flag == 0)
            {
                if (sub(nb_bits, NBBITS_9k) <= 0)
                {
                    index = Serial_parm(8, prms, ref CurrentPrmsIndex);
                    
                    if (sub(index, (PIT_FR1_8b - PIT_MIN) * 2) < 0)
                    {
                        T0 = add(PIT_MIN, shr(index, 1));
                        T0_frac = sub(index, shl(sub(T0, PIT_MIN), 1));
                        T0_frac = shl(T0_frac, 1);
                    }
                    else
                    {
                        T0 = add(index, PIT_FR1_8b - ((PIT_FR1_8b - PIT_MIN) * 2));
                        T0_frac = 0;
                    }
                }
                else
                {
                    index = Serial_parm(9, prms, ref CurrentPrmsIndex);
                    
                    if (sub(index, (PIT_FR2 - PIT_MIN) * 4) < 0)
                    {
                        T0 = add(PIT_MIN, shr(index, 2));
                        T0_frac = sub(index, shl(sub(T0, PIT_MIN), 2));
                    }
                    else if (sub(index, (((PIT_FR2 - PIT_MIN) * 4) + ((PIT_FR1_9b - PIT_FR2) * 2))) < 0)
                    {
                        index = sub(index, (PIT_FR2 - PIT_MIN) * 4);
                        T0 = add(PIT_FR2, shr(index, 1));
                        T0_frac = sub(index, shl(sub(T0, PIT_FR2), 1));
                        T0_frac = shl(T0_frac, 1);
                    }
                    else
                    {
                        T0 = add(index, (PIT_FR1_9b - ((PIT_FR2 - PIT_MIN) * 4) - ((PIT_FR1_9b - PIT_FR2) * 2)));
                        T0_frac = 0;
                    }
                }

                /* find T0_min and T0_max for subframe 2 and 4 */

                T0_min = sub(T0, 8);
                if (sub(T0_min, PIT_MIN) < 0)
                {
                    T0_min = PIT_MIN;
                }
                T0_max = add(T0_min, 15);
                
                if (sub(T0_max, PIT_MAX) > 0)
                {
                    T0_max = PIT_MAX;
                    T0_min = sub(T0_max, 15);
                }
            }
            else
            {                                  /* if subframe 2 or 4 */
                if (sub(nb_bits, NBBITS_9k) <= 0)
                {
                    index = Serial_parm(5, prms, ref CurrentPrmsIndex);

                    T0 = add(T0_min, shr(index, 1));
                    T0_frac = sub(index, shl(sub(T0, T0_min), 1));
                    T0_frac = shl(T0_frac, 1);
                }
                else
                {
                    index = Serial_parm(6, prms, ref CurrentPrmsIndex);

                    T0 = add(T0_min, shr(index, 2));
                    T0_frac = sub(index, shl(sub(T0, T0_min), 2));
                }
            }

            /* check BFI after pitch lag decoding */
            if (bfi != 0)                      /* if frame erasure */
            {
                //lagconc(&(st.dec_gain[17]), st->lag_hist, &T0, &(st->old_T0), &(st->seed3), unusable_frame);
                lagconc(st.dec_gain, 17, st.lag_hist, &T0, ref st.old_T0, ref st.seed3, unusable_frame);
                T0_frac = 0;
            }
            /*-------------------------------------------------*
             * - Find the pitch gain, the interpolation filter *
             *   and the adaptive codebook vector.             *
             *-------------------------------------------------*/

            Pred_lt4(&exc[i_subfr], T0, T0_frac, L_SUBFR + 1);

            if (unusable_frame != 0)
            {
                select = 1;
            }
            else
            {
                if (sub(nb_bits, NBBITS_9k) <= 0)
                {
                    select = 0;
                }
                else
                {
                    select = Serial_parm(1, prms, ref CurrentPrmsIndex);
                }
            }

            if (select == 0)
            {
                /* find pitch excitation with lp filter */
                for (i = 0; i < L_SUBFR; i++)
                {
                    L_tmp = L_mult(5898, exc[i - 1 + i_subfr]);
                    L_tmp = L_mac(L_tmp, 20972, exc[i + i_subfr]);
                    L_tmp = L_mac(L_tmp, 5898, exc[i + 1 + i_subfr]);
                    code[i] = round(L_tmp);
                }
                Copy(code, &exc[i_subfr], L_SUBFR);
            }
            /*-------------------------------------------------------*
             * - Decode innovative codebook.                         *
             * - Add the fixed-gain pitch contribution to code[].    *
             *-------------------------------------------------------*/
            if (unusable_frame != 0)
            {
                /* the innovative code doesn't need to be scaled (see Q_gain2) */
                for (i = 0; i < L_SUBFR; i++)
                {
                    code[i] = shr(Random(ref st.seed), 3);
                }
            }
            else if (sub(nb_bits, NBBITS_7k) <= 0)
            {
                ind[0] = Serial_parm(12, prms, ref CurrentPrmsIndex);
                DEC_ACELP_2t64_fx(ind[0], code);
            }
            else if (sub(nb_bits, NBBITS_9k) <= 0)
            {
                for (i = 0; i < 4; i++)
                {
                    ind[i] = Serial_parm(5, prms, ref CurrentPrmsIndex);
                }
                DEC_ACELP_4t64_fx(ind, 20, code);
            }
            else if (sub(nb_bits, NBBITS_12k) <= 0)
            {
                for (i = 0; i < 4; i++)
                {
                    ind[i] = Serial_parm(9, prms, ref CurrentPrmsIndex);
                }
                DEC_ACELP_4t64_fx(ind, 36, code);
            }
            else if (sub(nb_bits, NBBITS_14k) <= 0)
            {
                ind[0] = Serial_parm(13, prms, ref CurrentPrmsIndex);
                ind[1] = Serial_parm(13, prms, ref CurrentPrmsIndex);
                ind[2] = Serial_parm(9, prms, ref CurrentPrmsIndex);
                ind[3] = Serial_parm(9, prms, ref CurrentPrmsIndex);
                DEC_ACELP_4t64_fx(ind, 44, code);
            }
            else if (sub(nb_bits, NBBITS_16k) <= 0)
            {
                for (i = 0; i < 4; i++)
                {
                    ind[i] = Serial_parm(13, prms, ref CurrentPrmsIndex);
                }
                DEC_ACELP_4t64_fx(ind, 52, code);
            }
            else if (sub(nb_bits, NBBITS_18k) <= 0)
            {
                for (i = 0; i < 4; i++)
                {
                    ind[i] = Serial_parm(2, prms, ref CurrentPrmsIndex);
                }
                for (i = 4; i < 8; i++)
                {
                    ind[i] = Serial_parm(14, prms, ref CurrentPrmsIndex);
                }
                DEC_ACELP_4t64_fx(ind, 64, code);
            }
            else if (sub(nb_bits, NBBITS_20k) <= 0)
            {
                ind[0] = Serial_parm(10, prms, ref CurrentPrmsIndex);
                ind[1] = Serial_parm(10, prms, ref CurrentPrmsIndex);
                ind[2] = Serial_parm(2, prms, ref CurrentPrmsIndex);
                ind[3] = Serial_parm(2, prms, ref CurrentPrmsIndex);
                ind[4] = Serial_parm(10, prms, ref CurrentPrmsIndex);
                ind[5] = Serial_parm(10, prms, ref CurrentPrmsIndex);
                ind[6] = Serial_parm(14, prms, ref CurrentPrmsIndex);
                ind[7] = Serial_parm(14, prms, ref CurrentPrmsIndex);
                DEC_ACELP_4t64_fx(ind, 72, code);
            }
            else
            {
                for (i = 0; i < 4; i++)
                {
                    ind[i] = Serial_parm(11, prms, ref CurrentPrmsIndex);
                }
                for (i = 4; i < 8; i++)
                {
                    ind[i] = Serial_parm(11, prms, ref CurrentPrmsIndex);
                }
                DEC_ACELP_4t64_fx(ind, 88, code);
            }

            tmp = 0;
            Preemph(code, st.tilt_code, L_SUBFR, ref tmp);

            tmp = T0;
           
            if (sub(T0_frac, 2) > 0)
            {
                tmp = add(tmp, 1);
            }
            Pit_shrp(code, tmp, PIT_SHARP, L_SUBFR);

            /*-------------------------------------------------*
             * - Decode codebooks gains.                       *
             *-------------------------------------------------*/
            
            if (sub(nb_bits, NBBITS_9k) <= 0)
            {
                index = Serial_parm(6, prms, ref CurrentPrmsIndex); /* codebook gain index */

                D_gain2(index, 6, code, L_SUBFR, &gain_pit, &L_gain_code, bfi, st.prev_bfi, st.state, unusable_frame, 
                    st.vad_hist, st.dec_gain);
            }
            else
            {
                index = Serial_parm(7, prms, ref CurrentPrmsIndex); /* codebook gain index */

                D_gain2(index, 7, code, L_SUBFR, &gain_pit, &L_gain_code, bfi, st.prev_bfi, st.state, unusable_frame, 
                    st.vad_hist, st.dec_gain);
            }

            /* find best scaling to perform on excitation (Q_new) */

            tmp = st.Qsubfr[0];
            for (i = 1; i < 4; i++)
            {
                if (sub(st.Qsubfr[i], tmp) < 0)
                {
                    tmp = st.Qsubfr[i];
                }
            }

            /* limit scaling (Q_new) to Q_MAX: see cnst.h and syn_filt_32() */

            if (sub(tmp, Q_MAX) > 0)
            {
                tmp = Q_MAX;
            }
            Q_new = 0;
            L_tmp = L_gain_code;  /* L_gain_code in Q16 */

            while ((L_sub(L_tmp, 0x08000000) < 0) && (sub(Q_new, tmp) < 0))
            {
                L_tmp = L_shl(L_tmp, 1);
                Q_new = add(Q_new, 1);
            }
            gain_code = round(L_tmp);          /* scaled gain_code with Qnew */

            Scale_sig(exc + i_subfr - (PIT_MAX + L_INTERPOL),
                PIT_MAX + L_INTERPOL + L_SUBFR, sub(Q_new, st.Q_old));
            st.Q_old = Q_new;


            /*----------------------------------------------------------*
             * Update parameters for the next subframe.                 *
             * - tilt of code: 0.0 (unvoiced) to 0.5 (voiced)           *
             *----------------------------------------------------------*/

            if (bfi == 0)
            {
                /* LTP-Lag history update */
                for (i = 4; i > 0; i--)
                {
                    st.lag_hist[i] = st.lag_hist[i - 1];
                }
                st.lag_hist[0] = T0;

                st.old_T0 = T0;
                st.old_T0_frac = 0;  /* Remove fraction in case of BFI */
            }
            /* find voice factor in Q15 (1=voiced, -1=unvoiced) */

            Copy(&exc[i_subfr], exc2, L_SUBFR);
            Scale_sig(exc2, L_SUBFR, -3);

            /* post processing of excitation elements */
            if (sub(nb_bits, NBBITS_9k) <= 0)
            {
                pit_sharp = shl(gain_pit, 1);
                if (sub(pit_sharp, 16384) > 0)
                {
                    for (i = 0; i < L_SUBFR; i++)
                    {
                        tmp = mult(exc2[i], pit_sharp);
                        L_tmp = L_mult(tmp, gain_pit);
                        L_tmp = L_shr(L_tmp, 1);
                        excp[i] = round(L_tmp);
                    }
                }
            }
            else
            {
                pit_sharp = 0;
            }

            voice_fac = voice_factor(exc2, -3, gain_pit, code, gain_code, L_SUBFR);

            /* tilt of code for next subframe: 0.5=voiced, 0=unvoiced */

            st.tilt_code = add(shr(voice_fac, 2), 8192);

            /*-------------------------------------------------------*
             * - Find the total excitation.                          *
             * - Find dec_synthesis speech corresponding to exc[].       *
             *-------------------------------------------------------*/

            Copy(&exc[i_subfr], exc2, L_SUBFR);

            for (i = 0; i < L_SUBFR; i++)
            {
                L_tmp = L_mult(code[i], gain_code);
                L_tmp = L_shl(L_tmp, 5);
                L_tmp = L_mac(L_tmp, exc[i + i_subfr], gain_pit);
                L_tmp = L_shl(L_tmp, 1);
                exc[i + i_subfr] = round(L_tmp);
            }

            /* find maximum value of excitation for next scaling */

            max = 1;
            for (i = 0; i < L_SUBFR; i++)
            {
                tmp = abs_s(exc[i + i_subfr]);
                if (sub(tmp, max) > 0)
                {
                    max = tmp;
                }
            }

            /* tmp = scaling possible according to max value of excitation */
            tmp = sub(add(norm_s(max), Q_new), 1);

            st.Qsubfr[3] = st.Qsubfr[2];
            st.Qsubfr[2] = st.Qsubfr[1];
            st.Qsubfr[1] = st.Qsubfr[0];
            st.Qsubfr[0] = tmp;

            /*------------------------------------------------------------*
             * phase dispersion to enhance noise in low bit rate          *
             *------------------------------------------------------------*/

            /* L_gain_code in Q16 */
            gain_code_lo = 0;
            L_Extract(L_gain_code, ref gain_code, ref gain_code_lo);
            
            if (sub(nb_bits, NBBITS_7k) <= 0)
                j = 0;                         /* high dispersion for rate <= 7.5 kbit/s */
            else if (sub(nb_bits, NBBITS_9k) <= 0)
                j = 1;                         /* low dispersion for rate <= 9.6 kbit/s */
            else
                j = 2;                         /* no dispersion for rate > 9.6 kbit/s */

            Phase_dispersion(gain_code, gain_pit, code, j, st.disp_mem);

            /*------------------------------------------------------------*
             * noise enhancer                                             *
             * ~~~~~~~~~~~~~~                                             *
             * - Enhance excitation on noise. (modify gain of code)       *
             *   If signal is noisy and LPC filter is stable, move gain   *
             *   of code 1.5 dB toward gain of code threshold.            *
             *   This decrease by 3 dB noise energy variation.            *
             *------------------------------------------------------------*/

            tmp = sub(16384, shr(voice_fac, 1));    /* 1=unvoiced, 0=voiced */
            fac = mult(stab_fac, tmp);

            L_tmp = L_gain_code;
            if (L_sub(L_tmp, st.L_gc_thres) < 0)
            {
                L_tmp = L_add(L_tmp, Mpy_32_16(gain_code, gain_code_lo, 6226));
                
                if (L_sub(L_tmp, st.L_gc_thres) > 0)
                {
                    L_tmp = st.L_gc_thres;
                }
            }
            else
            {
                L_tmp = Mpy_32_16(gain_code, gain_code_lo, 27536);
                
                if (L_sub(L_tmp, st.L_gc_thres) < 0)
                {
                    L_tmp = st.L_gc_thres;
                }
            }
            st.L_gc_thres = L_tmp;

            L_gain_code = Mpy_32_16(gain_code, gain_code_lo, sub(32767, fac));
            L_Extract(L_tmp, ref gain_code, ref gain_code_lo);
            L_gain_code = L_add(L_gain_code, Mpy_32_16(gain_code, gain_code_lo, fac));

            /*------------------------------------------------------------*
             * pitch enhancer                                             *
             * ~~~~~~~~~~~~~~                                             *
             * - Enhance excitation on voice. (HP filtering of code)      *
             *   On voiced signal, filtering of code by a smooth fir HP   *
             *   filter to decrease energy of code in low frequency.      *
             *------------------------------------------------------------*/

            tmp = add(shr(voice_fac, 3), 4096);/* 0.25=voiced, 0=unvoiced */

            L_tmp = L_deposit_h(code[0]);
            L_tmp = L_msu(L_tmp, code[1], tmp);
            code2[0] = round(L_tmp);

            for (i = 1; i < L_SUBFR - 1; i++)
            {
                L_tmp = L_deposit_h(code[i]);
                L_tmp = L_msu(L_tmp, code[i + 1], tmp);
                L_tmp = L_msu(L_tmp, code[i - 1], tmp);
                code2[i] = round(L_tmp);
            }

            L_tmp = L_deposit_h(code[L_SUBFR - 1]);
            L_tmp = L_msu(L_tmp, code[L_SUBFR - 2], tmp);
            code2[L_SUBFR - 1] = round(L_tmp);

            /* build excitation */

            gain_code = round(L_shl(L_gain_code, Q_new));

            for (i = 0; i < L_SUBFR; i++)
            {
                L_tmp = L_mult(code2[i], gain_code);
                L_tmp = L_shl(L_tmp, 5);
                L_tmp = L_mac(L_tmp, exc2[i], gain_pit);
                L_tmp = L_shl(L_tmp, 1);       /* saturation can occur here */
                exc2[i] = round(L_tmp);
            }

            if (sub(nb_bits, NBBITS_9k) <= 0)
            {
                if (sub(pit_sharp, 16384) > 0)
                {
                    for (i = 0; i < L_SUBFR; i++)
                    {
                        excp[i] = add(excp[i], exc2[i]);
                    }
                    agc2(exc2, excp, L_SUBFR);
                    Copy(excp, exc2, L_SUBFR);
                }
            }
            if (sub(nb_bits, NBBITS_7k) <= 0)
            {
                j = shr(i_subfr, 6);
                for (i = 0; i < M; i++)
                {
                    L_tmp = L_mult(isf_tmp[i], sub(32767, interpol_frac[j]));
                    L_tmp = L_mac(L_tmp, isf[i], interpol_frac[j]);
                    HfIsf[i] = round(L_tmp);
                }
            }
            else
            {
                Set_zero(st.mem_syn_hf, M16k - M);
            }

            if (sub(nb_bits, NBBITS_24k) >= 0)
            {
                corr_gain = Serial_parm(4, prms, ref CurrentPrmsIndex);
                dec_synthesis(p_Aq, exc2, Q_new, &synth16k[(i_subfr * 5) / 4], corr_gain, HfIsf, nb_bits, newDTXState, st, bfi);
            }
            else
                dec_synthesis(p_Aq, exc2, Q_new, &synth16k[(i_subfr) * 5 / 4], 0, HfIsf, nb_bits, newDTXState, st, bfi);

            p_Aq += (M + 1);                   /* interpolated LPC parameters for next subframe */
        }

        /*--------------------------------------------------*
         * Update signal for next frame.                    *
         * -> save past of exc[].                           *
         * -> save pitch parameters.                        *
         *--------------------------------------------------*/

        Copy(&old_exc[L_FRAME], st.old_exc, PIT_MAX + L_INTERPOL);

        Scale_sig(exc, L_FRAME, sub(0, Q_new));
        dtx_dec_activity_update(st.dtx_decSt, isf, exc);

        st.dtx_decSt.dtxGlobalState = newDTXState;

        st.prev_bfi = bfi;

        return;
    }


    /*-----------------------------------------------------*
     * Function dec_synthesis()                                *
     *                                                     *
     * dec_synthesis of signal at 16kHz with HF extension.     *
     *                                                     *
     *-----------------------------------------------------*/

    private unsafe void dec_synthesis(
         short* Aq,                          /* A(z)  : quantized Az               */
         short* exc,                         /* (i)   : excitation at 12kHz        */
         short Q_new,                         /* (i)   : scaling performed on exc   */
         short* synth16k,                    /* (o)   : 16kHz dec_synthesis signal     */
         short prms,                          /* (i)   : parameter                  */
         short[] HfIsf,
         short nb_bits,
         short newDTXState,
         Decoder_State st,                   /* (i/o) : State structure            */
         short bfi                            /* (i)   : bad frame indicator        */
    )
    {
        short i, fac, tmp, exp;
        short ener, exp_ener;
        int L_tmp;

        short* synth_hi = stackalloc short[M + L_SUBFR];
        short* synth_lo = stackalloc short[M + L_SUBFR];
        short[] synth = new short[L_SUBFR];
        short* HF = stackalloc short[L_SUBFR16k];                 /* High Frequency vector      */
        short* Ap = stackalloc short[M16k + 1];
        short* HfA = stackalloc short[M16k + 1];
        short HF_corr_gain;
        short HF_gain_ind;
        short gain1, gain2;
        short weight1, weight2;

        // 19 May 25 PHR -- For debug only
        Span<short> Ap_Span = new Span<short>(Ap, M16k + 1);
        Span<short> HF_Span = new Span<short>(HF, L_SUBFR16k);
        Span<short> HFA_Span = new Span<short>(HfA, M16k + 1);

        /*------------------------------------------------------------*
         * speech dec_synthesis                                           *
         * ~~~~~~~~~~~~~~~~                                           *
         * - Find dec_synthesis speech corresponding to exc2[].           *
         * - Perform fixed deemphasis and hp 50hz filtering.          *
         * - Oversampling from 12.8kHz to 16kHz.                      *
         *------------------------------------------------------------*/

        Copy(st.mem_syn_hi, synth_hi, M);
        Copy(st.mem_syn_lo, synth_lo, M);

        Syn_filt_32(Aq, M, exc, Q_new, synth_hi + M, synth_lo + M, L_SUBFR);

        Copy(synth_hi + L_SUBFR, st.mem_syn_hi, M);
        Copy(synth_lo + L_SUBFR, st.mem_syn_lo, M);

        Deemph_32(synth_hi + M, synth_lo + M, synth, PREEMPH_FAC, L_SUBFR, ref st.mem_deemph);

        HP50_12k8(synth, L_SUBFR, st.mem_sig_out);

        Oversamp_16k(synth, L_SUBFR, synth16k, st.mem_oversamp);

        /*------------------------------------------------------*
        * HF noise dec_synthesis                                   *
        * ~~~~~~~~~~~~~~~~~~                                   *
        * - Generate HF noise between 5.5 and 7.5 kHz.         *
        * - Set energy of noise according to dec_synthesis tilt.   *
        *     tilt > 0.8 ==> - 14 dB (voiced)                  *
        *     tilt   0.5 ==> - 6 dB  (voiced or noise)         *
        *     tilt < 0.0 ==>   0 dB  (noise)                   *
        *------------------------------------------------------*/

        /* generate white noise vector */
        for (i = 0; i < L_SUBFR16k; i++)
        {
            HF[i] = shr(Random(ref st.seed2), 3);
        }
        /* energy of excitation */

        Scale_sig(exc, L_SUBFR, -3);
        Q_new = sub(Q_new, 3);

        exp_ener = 0;
        ener = extract_h(Dot_product12(exc, exc, L_SUBFR, ref exp_ener));
        exp_ener = sub(exp_ener, add(Q_new, Q_new));

        /* set energy of white noise to energy of excitation */
        exp = 0;
        tmp = extract_h(Dot_product12(HF, HF, L_SUBFR16k, ref exp));
        
        if (sub(tmp, ener) > 0)
        {
            tmp = shr(tmp, 1);                 /* Be sure tmp < ener */
            exp = add(exp, 1);
        }
        L_tmp = L_deposit_h(div_s(tmp, ener)); /* result is normalized */
        exp = sub(exp, exp_ener);
        Isqrt_n(ref L_tmp, ref exp);
        L_tmp = L_shl(L_tmp, add(exp, 1));     /* L_tmp x 2, L_tmp in Q31 */
        tmp = extract_h(L_tmp);                /* tmp = 2 x sqrt(ener_exc/ener_hf) */
        for (i = 0; i < L_SUBFR16k; i++)
        {
            HF[i] = mult(HF[i], tmp);
        }
        /* find tilt of dec_synthesis speech (tilt: 1=voiced, -1=unvoiced) */

        HP400_12k8(synth, L_SUBFR, st.mem_hp400);

        L_tmp = 1;
        for (i = 0; i < L_SUBFR; i++)
            L_tmp = L_mac(L_tmp, synth[i], synth[i]);

        exp = norm_l(L_tmp);
        ener = extract_h(L_shl(L_tmp, exp));   /* ener = r[0] */

        L_tmp = 1;
        for (i = 1; i < L_SUBFR; i++)
            L_tmp = L_mac(L_tmp, synth[i], synth[i - 1]);

        tmp = extract_h(L_shl(L_tmp, exp));    /* tmp = r[1] */
        
        if (tmp > 0)
        {
            fac = div_s(tmp, ener);
        }
        else
        {
            fac = 0;
        }

        /* modify energy of white noise according to dec_synthesis tilt */
        gain1 = sub(32767, fac);
        gain2 = mult(sub(32767, fac), 20480);
        gain2 = shl(gain2, 1);

        if (st.vad_hist > 0)
        {
            weight1 = 0;
            weight2 = 32767;
        }
        else
        {
            weight1 = 32767;
            weight2 = 0;
        }
        tmp = mult(weight1, gain1);
        tmp = add(tmp, mult(weight2, gain2));

        if (tmp != 0)
        {
            tmp = add(tmp, 1);
        }
        
        if (sub(tmp, 3277) < 0)
        {
            tmp = 3277;                        /* 0.1 in Q15 */
        }

        if ((sub(nb_bits, NBBITS_24k) >= 0) && (bfi == 0))
        {
            /* HF correction gain */
            HF_gain_ind = prms;
            HF_corr_gain = HP_gain[HF_gain_ind];

            /* HF gain */
            for (i = 0; i < L_SUBFR16k; i++)
            {
                HF[i] = shl(mult(HF[i], HF_corr_gain), 1);
            }
        }
        else
        {
            for (i = 0; i < L_SUBFR16k; i++)
            {
                HF[i] = mult(HF[i], tmp);
            }
        }

        if ((sub(nb_bits, NBBITS_7k) <= 0) && (sub(newDTXState, SPEECH) == 0))
        {
            Isf_Extrapolation(HfIsf);
            Isp_Az(HfIsf, HfA, M16k, 0);

            Weight_a(HfA, Ap, 29491, M16k);    /* fac=0.9 */
            Syn_filt(Ap, M16k, HF, HF, L_SUBFR16k, st.mem_syn_hf, 1);
        }
        else
        {
            /* dec_synthesis of noise: 4.8kHz..5.6kHz --> 6kHz..7kHz */
            Weight_a(Aq, Ap, 19661, M);        /* fac=0.6 */
            Syn_filt(Ap, M, HF, HF, L_SUBFR16k, st.mem_syn_hf, (M16k - M), 1);
        }

        /* noise High Pass filtering (1ms of delay) */
        Filt_6k_7k(HF, L_SUBFR16k, st.mem_hf);

        if (sub(nb_bits, NBBITS_24k) >= 0)
        {
            /* Low Pass filtering (7 kHz) */
            Filt_7k(HF, L_SUBFR16k, st.mem_hf3);
        }
        /* add filtered HF noise to speech dec_synthesis */
        for (i = 0; i < L_SUBFR16k; i++)
        {
            synth16k[i] = add(synth16k[i], HF[i]);
        }

        return;
    }
}
