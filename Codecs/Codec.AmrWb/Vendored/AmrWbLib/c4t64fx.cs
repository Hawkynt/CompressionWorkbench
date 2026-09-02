/*------------------------------------------------------------------------*
 *                         C4T64FX.C                    *
 *------------------------------------------------------------------------*
 * Performs algebraic codebook search for higher modes                    *
 *------------------------------------------------------------------------*/


/*-----------------------------------------------------------------------*
 * Function  ACELP_4t64_fx()                                             *
 * ~~~~~~~~~~~~~~~~~~~~~~~~~                                             *
 * 20, 36, 44, 52, 64, 72, 88 bits algebraic codebook.                   *
 * 4 tracks x 16 positions per track = 64 samples.                       *
 *                                                                       *
 * 20 bits --> 4 pulses in a frame of 64 samples.                        *
 * 36 bits --> 8 pulses in a frame of 64 samples.                        *
 * 44 bits --> 10 pulses in a frame of 64 samples.                       *
 * 52 bits --> 12 pulses in a frame of 64 samples.                       *
 * 64 bits --> 16 pulses in a frame of 64 samples.                       *
 * 72 bits --> 18 pulses in a frame of 64 samples.                       *
 * 88 bits --> 24 pulses in a frame of 64 samples.                       *
 *                                                                       *
 * All pulses can have two (2) possible amplitudes: +1 or -1.            *
 * Each pulse can have sixteen (16) possible positions.                  *
 *-----------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "math_op.h"
//#include "acelp.h"
//#include "count.h"
//#include "cnst.h"
//#include "q_pulse.h"

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{

    private static short[] tipos = new short[36] {
    0, 1, 2, 3,                            /* starting point &ipos[0], 1st iter */
    1, 2, 3, 0,                            /* starting point &ipos[4], 2nd iter */
    2, 3, 0, 1,                            /* starting point &ipos[8], 3rd iter */
    3, 0, 1, 2,                            /* starting point &ipos[12], 4th iter */
    0, 1, 2, 3,
    1, 2, 3, 0,
    2, 3, 0, 1,
    3, 0, 1, 2,
    0, 1, 2, 3};                           /* end point for 24 pulses &ipos[35], 4th iter */


//#define NB_PULSE_MAX  24
//#define L_SUBFR   64
//#define NB_TRACK  4
//#define STEP      4
//#define NB_POS    16
//#define MSIZE     256
//#define NB_MAX    8
//#define NPMAXPT   ((NB_PULSE_MAX+NB_TRACK-1)/NB_TRACK)

    private unsafe void ACELP_4t64_fx(
        short* dn,                          /* (i) <12b : correlation between target x[] and H[]      */
        short* cn,                          /* (i) <12b : residual after long term prediction         */
        short* H,                           /* (i) Q12: impulse response of weighted synthesis filter */
        short* code,                        /* (o) Q9 : algebraic (fixed) codebook excitation         */
        short* y,                           /* (o) Q9 : filtered fixed codebook excitation            */
        short nbbits,                        /* (i) : 20, 36, 44, 52, 64, 72 or 88 bits                */
        short ser_size,                      /* (i) : bit rate                                         */
        short[] _index)                      /* (o) : index (20): 5+5+5+5 = 20 bits.                   */
                                        /* (o) : index (36): 9+9+9+9 = 36 bits.                   */
                                        /* (o) : index (44): 13+9+13+9 = 44 bits.                 */
                                        /* (o) : index (52): 13+13+13+13 = 52 bits.               */
                                        /* (o) : index (64): 2+2+2+2+14+14+14+14 = 64 bits.       */
                                        /* (o) : index (72): 10+2+10+2+10+14+10+14 = 72 bits.     */
                                        /* (o) : index (88): 11+11+11+11+11+11+11+11 = 88 bits.   */
    {
        int NB_PULSE_MAX = 24;
        int L_SUBFR = 64;
        int NB_TRACK = 4;
        short STEP = 4;
        int NB_POS = 16;
        int MSIZE = 256;
        int NB_MAX = 8;
        int NPMAXPT = ((NB_PULSE_MAX + NB_TRACK - 1) / NB_TRACK);

        short i, j, k, st, ix, iy, pos, index, track, nb_pulse, nbiter;
        short psk, ps, alpk, alp, val, k_cn, k_dn, exp;
        short* p0, p1, p2, p3, psign;
        short p0_offset;
        short* h, h_inv, ptr_h1, ptr_h2, ptr_hf;
        short h_shift;
        int s, cor, L_tmp, L_index;

        //short dn2[L_SUBFR], sign[L_SUBFR], vec[L_SUBFR];
        //short ind[NPMAXPT * NB_TRACK];
        //short codvec[NB_PULSE_MAX], nbpos[10];
        //short cor_x[NB_POS], cor_y[NB_POS], pos_max[NB_TRACK];
        //short h_buf[4 * L_SUBFR];
        //short rrixix[NB_TRACK][NB_POS], rrixiy[NB_TRACK][MSIZE];
        //short ipos[NB_PULSE_MAX];

        short* dn2 = stackalloc short[L_SUBFR];
        short* sign = stackalloc short[L_SUBFR];
        short* vec = stackalloc short[L_SUBFR];
        short* ind = stackalloc short[NPMAXPT * NB_TRACK];
        short* codvec = stackalloc short[NB_PULSE_MAX];
        short* nbpos = stackalloc short[10];
        short* cor_x = stackalloc short[NB_POS];
        short* cor_y = stackalloc short[NB_POS];
        short* pos_max = stackalloc short[NB_TRACK];
        short* h_buf = stackalloc short[4 * L_SUBFR];

        short* rrixix = stackalloc short[NB_TRACK * NB_POS];
        short* rrixiy = stackalloc short[NB_TRACK * MSIZE];
        short* ipos = stackalloc short[NB_PULSE_MAX];

        switch (nbbits)
        {
            case 20:          /* 20 bits, 4 pulses, 4 tracks */
                nbiter = 4;   /* 4x16x16=1024 loop */
                alp = 8192;   /* alp = 2.0 (Q12) */
                nb_pulse = 4; 
                nbpos[0] = 4; 
                nbpos[1] = 8; 
                break;
            case 36:          /* 36 bits, 8 pulses, 4 tracks */
                nbiter = 4;   /* 4x20x16=1280 loop */
                alp = 4096;   /* alp = 1.0 (Q12) */
                nb_pulse = 8; 
                nbpos[0] = 4; 
                nbpos[1] = 8; 
                nbpos[2] = 8; 
                break;
            case 44:          /* 44 bits, 10 pulses, 4 tracks */
                nbiter = 4;   /* 4x26x16=1664 loop */
                alp = 4096;   /* alp = 1.0 (Q12) */
                nb_pulse = 10; 
                nbpos[0] = 4; 
                nbpos[1] = 6; 
                nbpos[2] = 8; 
                nbpos[3] = 8; 
                break;
            case 52:          /* 52 bits, 12 pulses, 4 tracks */
                nbiter = 4;   /* 4x26x16=1664 loop */
                alp = 4096;   /* alp = 1.0 (Q12) */
                nb_pulse = 12; 
                nbpos[0] = 4; 
                nbpos[1] = 6; 
                nbpos[2] = 8; 
                nbpos[3] = 8; 
                break;
            case 64:          /* 64 bits, 16 pulses, 4 tracks */
                nbiter = 3;   /* 3x36x16=1728 loop */
                alp = 3277;   /* alp = 0.8 (Q12) */
                nb_pulse = 16; 
                nbpos[0] = 4; 
                nbpos[1] = 4; 
                nbpos[2] = 6; 
                nbpos[3] = 6; 
                nbpos[4] = 8; 
                nbpos[5] = 8; 
                break;
            case 72:          /* 72 bits, 18 pulses, 4 tracks */
                nbiter = 3;   /* 3x35x16=1680 loop */
                alp = 3072;   /* alp = 0.75 (Q12) */
                nb_pulse = 18; 
                nbpos[0] = 2; 
                nbpos[1] = 3; 
                nbpos[2] = 4; 
                nbpos[3] = 5; 
                nbpos[4] = 6; 
                nbpos[5] = 7; 
                nbpos[6] = 8; 
                break;
            case 88:          /* 88 bits, 24 pulses, 4 tracks */
                 
                if (sub(ser_size, 462) > 0)
                    nbiter = 1;
                else
                    nbiter = 2;  /* 2x53x16=1696 loop */

                alp = 2048;   /* alp = 0.5 (Q12) */
                nb_pulse = 24; 
                nbpos[0] = 2; 
                nbpos[1] = 2; 
                nbpos[2] = 3; 
                nbpos[3] = 4; 
                nbpos[4] = 5; 
                nbpos[5] = 6; 
                nbpos[6] = 7; 
                nbpos[7] = 8; 
                nbpos[8] = 8; 
                nbpos[9] = 8; 
                break;
            default:
                nbiter = 0;
                alp = 0;
                nb_pulse = 0;
                break;
        }

        for (i = 0; i < nb_pulse; i++)
        {
            codvec[i] = i; 
        }

        /*----------------------------------------------------------------*
         * Find sign for each pulse position.                             *
         *----------------------------------------------------------------*/

        /* calculate energy for normalization of cn[] and dn[] */

        /* set k_cn = 32..32767 (ener_cn = 2^30..256-0) */
        exp = 0;
        s = Dot_product12(cn, cn, (short) L_SUBFR, ref exp);
        Isqrt_n(ref s, ref exp);
        s = L_shl(s, add(exp, 5));             /* saturation can occur here */
        k_cn = round(s);

        /* set k_dn = 32..512 (ener_dn = 2^30..2^22) */
        s = Dot_product12(dn, dn, (short)L_SUBFR, ref exp);
        Isqrt_n(ref s, ref exp);
        k_dn = round(L_shl(s, add(exp, 5 + 3)));    /* k_dn = 256..4096 */
        k_dn = mult_r(alp, k_dn);              /* alp in Q12 */

        /* mix normalized cn[] and dn[] */
        for (i = 0; i < L_SUBFR; i++)
        {
            s = L_mac(L_mult(k_cn, cn[i]), k_dn, dn[i]);
            dn2[i] = extract_h(L_shl(s, 8)); 
        }

        /* set sign according to dn2[] = k_cn*cn[] + k_dn*dn[]    */

        for (k = 0; k < NB_TRACK; k++)
        {
            for (i = k; i < L_SUBFR; i += STEP)
            {
                val = dn[i]; 
                ps = dn2[i]; 

                
                if (ps >= 0)
                {
                    sign[i] = 32767;   /* sign = +1 (Q12) */
                    vec[i] = -32768; 
                } else
                {
                    sign[i] = -32768;   /* sign = -1 (Q12) */
                    vec[i] = 32767; 
                    val = negate(val);
                    ps = negate(ps);
                }
                dn[i] = val;   /* modify dn[] according to the fixed sign */
                dn2[i] = ps;   /* dn2[] = mix of dn[] and cn[]            */
            }
        }

        /*----------------------------------------------------------------*
         * Select NB_MAX position per track according to max of dn2[].    *
         *----------------------------------------------------------------*/

        pos = 0;
        for (i = 0; i < NB_TRACK; i++)
        {
            for (k = 0; k < NB_MAX; k++)
            {
                ps = -1; 
                for (j = i; j < L_SUBFR; j += STEP)
                {
                    
                    if (sub(dn2[j], ps) > 0)
                    {
                        ps = dn2[j]; 
                        pos = j; 
                    }
                }
                
                dn2[pos] = sub(k, (short)NB_MAX);     /* dn2 < 0 when position is selected */
                
                if (k == 0)
                {
                    pos_max[i] = pos; 
                }
            }
        }

        /*--------------------------------------------------------------*
         * Scale h[] to avoid overflow and to get maximum of precision  *
         * on correlation.                                              *
         *                                                              *
         * Maximum of h[] (h[0]) is fixed to 2048 (MAX16 / 16).         *
         *  ==> This allow addition of 16 pulses without saturation.    *
         *                                                              *
         * Energy worst case (on resonant impulse response),            *
         * - energy of h[] is approximately MAX/16.                     *
         * - During search, the energy is divided by 8 to avoid         *
         *   overflow on "alp". (energy of h[] = MAX/128).              *
         *  ==> "alp" worst case detected is 22854 on sinusoidal wave.  *
         *--------------------------------------------------------------*/

        /* impulse response buffer for fast computation */

        h = h_buf; 
        h_inv = h_buf + (2 * L_SUBFR); 
        for (i = 0; i < L_SUBFR; i++)
        {
            *h++ = 0; 
            *h_inv++ = 0; 
        }

        /* scale h[] down (/2) when energy of h[] is high with many pulses used */
        L_tmp = 0;
        for (i = 0; i < L_SUBFR; i++)
            L_tmp = L_mac(L_tmp, H[i], H[i]);
        val = extract_h(L_tmp);

        h_shift = 0; 
         
        if ((sub(nb_pulse, 12) >= 0) && (sub(val, 1024) > 0))
        {
            h_shift = 1; 
        }
        if ((sub(val, 0x6000) > 0))
        {
            h_shift = 2; 
        }
        for (i = 0; i < L_SUBFR; i++)
        {
            h[i] = shr(H[i], h_shift); 
            h_inv[i] = negate(h[i]); 
        }

        /*------------------------------------------------------------*
         * Compute rrixix[][] needed for the codebook search.         *
         * This algorithm compute impulse response energy of all      *
         * positions (16) in each track (4).       Total = 4x16 = 64. *
         *------------------------------------------------------------*/

        /* storage order --> i3i3, i2i2, i1i1, i0i0 */

        /* Init pointers to last position of rrixix[] */
        //p0 = &rrixix[0][NB_POS - 1]; 
        //p1 = &rrixix[1][NB_POS - 1]; 
        //p2 = &rrixix[2][NB_POS - 1]; 
        //p3 = &rrixix[3][NB_POS - 1]; 
        p0 = &rrixix[NB_POS - 1];
        p1 = &rrixix[1 * NB_POS + NB_POS - 1];
        p2 = &rrixix[2 * NB_POS + NB_POS - 1];
        p3 = &rrixix[3 * NB_POS + NB_POS - 1];

        ptr_h1 = h; 
        cor = 0x00008000;   /* for rounding */
        for (i = 0; i < NB_POS; i++)
        {
            cor = L_mac(cor, *ptr_h1, *ptr_h1);
            ptr_h1++;
            *p3-- = extract_h(cor); 
            cor = L_mac(cor, *ptr_h1, *ptr_h1);
            ptr_h1++;
            *p2-- = extract_h(cor); 
            cor = L_mac(cor, *ptr_h1, *ptr_h1);
            ptr_h1++;
            *p1-- = extract_h(cor); 
            cor = L_mac(cor, *ptr_h1, *ptr_h1);
            ptr_h1++;
            *p0-- = extract_h(cor); 
        }

        /*------------------------------------------------------------*
         * Compute rrixiy[][] needed for the codebook search.         *
         * This algorithm compute correlation between 2 pulses        *
         * (2 impulses responses) in 4 possible adjacents tracks.     *
         * (track 0-1, 1-2, 2-3 and 3-0).     Total = 4x16x16 = 1024. *
         *------------------------------------------------------------*/

        /* storage order --> i2i3, i1i2, i0i1, i3i0 */

        pos = (short) (MSIZE - 1); 
        ptr_hf = h + 1; 
        p0_offset = (short) -NB_POS; 

        for (k = 0; k < NB_POS; k++)
        {
            //p3 = &rrixiy[2][pos]; 
            //p2 = &rrixiy[1][pos]; 
            //p1 = &rrixiy[0][pos]; 
            //p0 = &rrixiy[3][pos];

            //p3 = &rrixiy[2 * NB_POS + pos];
            //p2 = &rrixiy[1 * NB_POS + pos];
            //p1 = &rrixiy[0 * NB_POS + pos];
            //p0 = &rrixiy[3 * NB_POS + pos];
            // 11 May 25 PHR -- rrixiy is NB_TRACK * MSIZE
            p3 = &rrixiy[2 * MSIZE + pos];
            p2 = &rrixiy[1 * MSIZE + pos];
            p1 = &rrixiy[0 * MSIZE + pos];
            p0 = &rrixiy[3 * MSIZE + pos];


            cor = 0x00008000;   /* for rounding */
            ptr_h1 = h; 
            ptr_h2 = ptr_hf; 

            for (i = add(k, 1); i < NB_POS; i++)
            {
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p3 = extract_h(cor); 
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p2 = extract_h(cor); 
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p1 = extract_h(cor); 
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *(p0 + p0_offset) = extract_h(cor); 

                p3 -= (NB_POS + 1);
                p2 -= (NB_POS + 1);
                p1 -= (NB_POS + 1);
                p0 -= (NB_POS + 1);
            }
            cor = L_mac(cor, *ptr_h1, *ptr_h2);
            ptr_h1++;
            ptr_h2++;
            *p3 = extract_h(cor); 
            cor = L_mac(cor, *ptr_h1, *ptr_h2);
            ptr_h1++;
            ptr_h2++;
            *p2 = extract_h(cor); 
            cor = L_mac(cor, *ptr_h1, *ptr_h2);
            ptr_h1++;
            ptr_h2++;
            *p1 = extract_h(cor); 

            pos -= (short)NB_POS;
            ptr_hf += STEP;
        }

        /* storage order --> i3i0, i2i3, i1i2, i0i1 */

        pos = (short) (MSIZE - 1); 
        ptr_hf = h + 3; 

        for (k = 0; k < NB_POS; k++)
        {
            //p3 = &rrixiy[3][pos]; 
            //p2 = &rrixiy[2][pos - 1]; 
            //p1 = &rrixiy[1][pos - 1]; 
            //p0 = &rrixiy[0][pos - 1]; 

            //p3 = &rrixiy[3 * NB_POS + pos];
            //p2 = &rrixiy[2 * NB_POS + pos - 1];
            //p1 = &rrixiy[1 * NB_POS + pos - 1];
            //p0 = &rrixiy[0 * NB_POS + pos - 1];
            // 11 May 25 PHR -- rrixir is NB_TRACK * MSIZE 
            p3 = &rrixiy[3 * MSIZE + pos];
            p2 = &rrixiy[2 * MSIZE + pos - 1];
            p1 = &rrixiy[1 * MSIZE + pos - 1];
            p0 = &rrixiy[0 * MSIZE + pos - 1];

            cor = 0x00008000;   /* for rounding */
            ptr_h1 = h; 
            ptr_h2 = ptr_hf; 

            for (i = add(k, 1); i < NB_POS; i++)
            {
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p3 = extract_h(cor); 
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p2 = extract_h(cor); 
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p1 = extract_h(cor); 
                cor = L_mac(cor, *ptr_h1, *ptr_h2);
                ptr_h1++;
                ptr_h2++;
                *p0 = extract_h(cor); 

                p3 -= (NB_POS + 1);
                p2 -= (NB_POS + 1);
                p1 -= (NB_POS + 1);
                p0 -= (NB_POS + 1);
            }
            cor = L_mac(cor, *ptr_h1, *ptr_h2);
            ptr_h1++;
            ptr_h2++;
            *p3 = extract_h(cor); 

            pos--;
            ptr_hf += STEP;
        }

        /*------------------------------------------------------------*
         * Modification of rrixiy[][] to take signs into account.     *
         *------------------------------------------------------------*/

        //p0 = &rrixiy[0][0];
        p0 = rrixiy;

        for (k = 0; k < NB_TRACK; k++)
        {
            for (i = k; i < L_SUBFR; i += STEP)
            {
                psign = sign; 
                
                if (psign[i] < 0)
                {
                    psign = vec; 
                }
                for (j = (short)((k + 1) % NB_TRACK); j < L_SUBFR; j += STEP)
                {
                    *p0 = mult(*p0, psign[j]); 
                    p0++;
                }
            }
        }

        /*-------------------------------------------------------------------*
         *                       Deep first search                           *
         *-------------------------------------------------------------------*/

        psk = -1; 
        alpk = 1; 

        for (k = 0; k < nbiter; k++)
        {
            for (i = 0; i < nb_pulse; i++)
                ipos[i] = tipos[(k * 4) + i];

              
            if (sub(nbbits, 20) == 0)
            {
                pos = 0; 
                ps = 0; 
                alp = 0; 
                for (i = 0; i < L_SUBFR; i++)
                {
                    vec[i] = 0; 
                }
            } else if ((sub(nbbits, 36) == 0) || (sub(nbbits, 44) == 0))
            {
                /* first stage: fix 2 pulses */
                pos = 2;

                ix = ind[0] = pos_max[ipos[0]];  
                iy = ind[1] = pos_max[ipos[1]];  
                ps = add(dn[ix], dn[iy]);
                i = shr(ix, 2);                /* ix / STEP */
                j = shr(iy, 2);                /* iy / STEP */

                //s = L_mult(rrixix[ipos[0]][i], 4096);
                //s = L_mac(s, rrixix[ipos[1]][j], 4096);
                s = L_mult(rrixix[ipos[0] * NB_POS + i], 4096);
                s = L_mac(s, rrixix[ipos[1] * NB_POS + j], 4096);

                i = add(shl(i, 4), j);         /* (ix/STEP)*NB_POS + (iy/STEP) */
                //s = L_mac(s, rrixiy[ipos[0]][i], 8192);
                //s = L_mac(s, rrixiy[ipos[0] * NB_POS + i], 8192);
                // 11 May 25 PHR
                s = L_mac(s, rrixiy[ipos[0] * MSIZE + i], 8192);

                alp = round(s);
                  
                if (sign[ix] < 0)
                    p0 = h_inv - ix;
                else
                    p0 = h - ix;
                  
                if (sign[iy] < 0)
                    p1 = h_inv - iy;
                else
                    p1 = h - iy;

                for (i = 0; i < L_SUBFR; i++)
                {
                    vec[i] = add(*p0++, *p1++); 
                }

                
                if (sub(nbbits, 44) == 0)
                {
                    ipos[8] = 0; 
                    ipos[9] = 1; 
                }
            } else
            {
                /* first stage: fix 4 pulses */
                pos = 4;

                ix = ind[0] = pos_max[ipos[0]];  
                iy = ind[1] = pos_max[ipos[1]];  
                i = ind[2] = pos_max[ipos[2]];  
                j = ind[3] = pos_max[ipos[3]];  
                ps = add(add(add(dn[ix], dn[iy]), dn[i]), dn[j]);

                  
                if (sign[ix] < 0)
                    p0 = h_inv - ix;
                else
                    p0 = h - ix;
                  
                if (sign[iy] < 0)
                    p1 = h_inv - iy;
                else
                    p1 = h - iy;
                  
                if (sign[i] < 0)
                    p2 = h_inv - i;
                else
                    p2 = h - i;
                  
                if (sign[j] < 0)
                    p3 = h_inv - j;
                else
                    p3 = h - j;

                for (i = 0; i < L_SUBFR; i++)
                {
                    vec[i] = add(add(add(*p0++, *p1++), *p2++), *p3++);
                    
                }

                L_tmp = 0; 
                for (i = 0; i < L_SUBFR; i++)
                    L_tmp = L_mac(L_tmp, vec[i], vec[i]);

                alp = round(L_shr(L_tmp, 3));

                if (sub(nbbits, 72) == 0)
                {
                    ipos[16] = 0; 
                    ipos[17] = 1; 
                }
            }

            /* other stages of 2 pulses */

            for (j = pos, st = 0; j < nb_pulse; j += 2, st++)
            {
                /*--------------------------------------------------*
                * Calculate correlation of all possible positions  *
                * of the next 2 pulses with previous fixed pulses. *
                * Each pulse can have 16 possible positions.       *
                *--------------------------------------------------*/

                cor_h_vec(h, vec, ipos[j], sign, rrixix, cor_x, NB_POS, STEP);
                cor_h_vec(h, vec, ipos[j + 1], sign, rrixix, cor_y, NB_POS, STEP);

                /*--------------------------------------------------*
                * Find best positions of 2 pulses.                 *
                *--------------------------------------------------*/

                search_ixiy(nbpos[st], ipos[j], ipos[j + 1], &ps, &alp,
                    &ix, &iy, dn, dn2, cor_x, cor_y, rrixiy);

                ind[j] = ix; 
                ind[j + 1] = iy; 

                  
                if (sign[ix] < 0)
                    p0 = h_inv - ix;
                else
                    p0 = h - ix;
                  
                if (sign[iy] < 0)
                    p1 = h_inv - iy;
                else
                    p1 = h - iy;

                for (i = 0; i < L_SUBFR; i++)
                {
                    vec[i] = add(vec[i], add(*p0++, *p1++));        /* can saturate here. */
                    
                }
            }

            /* memorise the best codevector */

            ps = mult(ps, ps);
            s = L_msu(L_mult(alpk, ps), psk, alp);
            
            if (s > 0)
            {
                psk = ps; 
                alpk = alp; 
                for (i = 0; i < nb_pulse; i++)
                {
                    codvec[i] = ind[i]; 
                }
                for (i = 0; i < L_SUBFR; i++)
                {
                    y[i] = vec[i]; 
                }
            }
        }

        /*-------------------------------------------------------------------*
         * Build the codeword, the filtered codeword and index of codevector.*
         *-------------------------------------------------------------------*/

        for (i = 0; i < NPMAXPT * NB_TRACK; i++)
        {
            ind[i] = -1; 
        }
        for (i = 0; i < L_SUBFR; i++)
        {
            code[i] = 0; 
            y[i] = shr_r(y[i], 3);   /* Q12 to Q9 */
        }

        val = shr(512, h_shift);               /* codeword in Q9 format */

        for (k = 0; k < nb_pulse; k++)
        {
            i = codvec[k];   /* read pulse position */
            j = sign[i];   /* read sign           */

            index = shr(i, 2);           /* index = pos of pulse (0..15) */
            track = (short)(i & 0x03);   /* track = i % NB_TRACK (0..3)  */

            if (j > 0)
            {
                code[i] = add(code[i], val); 
                codvec[k] = add(codvec[k], (short)(2 * L_SUBFR)); 
            } else
            {
                code[i] = sub(code[i], val); 
                index = add(index, (short)NB_POS); 
            }

            i = extract_l(L_shr(L_mult(track, (short)NPMAXPT), 1));

             
            while (ind[i] >= 0)
            {
                i = add(i, 1);
            }
            ind[i] = index; 
        }

        k = 0; 
        /* Build index of codevector */
              
        if (sub(nbbits, 20) == 0)
        {
            for (track = 0; track < NB_TRACK; track++)
            {
                _index[track] = extract_l(quant_1p_N1(ind[k], 4));
                k += (short)NPMAXPT;
            }
        } else if (sub(nbbits, 36) == 0)
        {
            for (track = 0; track < NB_TRACK; track++)
            {
                _index[track] = extract_l(quant_2p_2N1(ind[k], ind[k + 1], 4));
                k += (short)NPMAXPT;
            }
        } else if (sub(nbbits, 44) == 0)
        {
            for (track = 0; track < NB_TRACK - 2; track++)
            {
                _index[track] = extract_l(quant_3p_3N1(ind[k], ind[k + 1], ind[k + 2], 4));
                k += (short)NPMAXPT;
            }
            for (track = 2; track < NB_TRACK; track++)
            {
                _index[track] = extract_l(quant_2p_2N1(ind[k], ind[k + 1], 4));
                k += (short)NPMAXPT;
            }
        } else if (sub(nbbits, 52) == 0)
        {
            for (track = 0; track < NB_TRACK; track++)
            {
                _index[track] = extract_l(quant_3p_3N1(ind[k], ind[k + 1], ind[k + 2], 4));
                k += (short)NPMAXPT;
            }
        } else if (sub(nbbits, 64) == 0)
        {
            for (track = 0; track < NB_TRACK; track++)
            {
                L_index = quant_4p_4N(&ind[k], 4);
                _index[track] = extract_l(L_shr(L_index, 14) & 3);
                _index[track + NB_TRACK] = extract_l(L_index & 0x3FFF);
                k += (short)NPMAXPT;
            }
        } else if (sub(nbbits, 72) == 0)
        {
            for (track = 0; track < NB_TRACK - 2; track++)
            {
                L_index = quant_5p_5N(&ind[k], 4);
                _index[track] = extract_l(L_shr(L_index, 10) & 0x03FF);
                _index[track + NB_TRACK] = extract_l(L_index & 0x03FF);
                k += (short)NPMAXPT;
            }
            for (track = 2; track < NB_TRACK; track++)
            {
                L_index = quant_4p_4N(&ind[k], 4);
                _index[track] = extract_l(L_shr(L_index, 14) & 3);
                _index[track + NB_TRACK] = extract_l(L_index & 0x3FFF);
                k += (short)NPMAXPT;
            }
        } else if (sub(nbbits, 88) == 0)
        {
            for (track = 0; track < NB_TRACK; track++)
            {
                L_index = quant_6p_6N_2(&ind[k], 4);
                _index[track] = extract_l(L_shr(L_index, 11) & 0x07FF);
                _index[track + NB_TRACK] = extract_l(L_index & 0x07FF);
                k += (short)NPMAXPT;
            }
        }
        return;
    }


    /*-------------------------------------------------------------------*
     * Function  cor_h_vec()                                             *
     * ~~~~~~~~~~~~~~~~~~~~~                                             *
     * Compute correlations of h[] with vec[] for the specified track.   *
     *-------------------------------------------------------------------*/
    private unsafe void cor_h_vec(
         short* h,                           /* (i) scaled impulse response                 */
         short* vec,                         /* (i) scaled vector (/8) to correlate with h[] */
         short track,                         /* (i) track to use                            */
         short* sign,                        /* (i) sign vector                             */
         //short rrixix[][NB_POS],           /* (i) correlation of h[x] with h[x]      */
         short* rrixix,                       /* (i) correlation of h[x] with h[x]      */
         short* cor,                         /* (o) result of correlation (NB_POS elements) */
         int NB_POS,
         int STEP
         )
    {
        short i, j, pos, corr;
        short* p0, p1, p2;
        int L_sum;

        //p0 = rrixix[track];
        p0 = &rrixix[track * NB_POS];

        pos = track; 
        for (i = 0; i < NB_POS; i++, pos += (short)STEP)
        {
            L_sum = 0; 
            p1 = h; 
            p2 = &vec[pos]; 
            for (j = pos; j < L_SUBFR; j++)
                L_sum = L_mac(L_sum, *p1++, *p2++);

            L_sum = L_shl(L_sum, 1);

            corr = round(L_sum);

            cor[i] = add(mult(corr, sign[pos]), *p0++); 
        }

        return;
    }


    /*-------------------------------------------------------------------*
     * Function  search_ixiy()                                           *
     * ~~~~~~~~~~~~~~~~~~~~~~~                                           *
     * Find the best positions of 2 pulses in a subframe.                *
     *-------------------------------------------------------------------*/

    private unsafe void search_ixiy(
         short nb_pos_ix,                     /* (i) nb of pos for pulse 1 (1..8)       */
         short track_x,                       /* (i) track of pulse 1                   */
         short track_y,                       /* (i) track of pulse 2                   */
         short* ps,                          /* (i/o) correlation of all fixed pulses  */
         short* alp,                         /* (i/o) energy of all fixed pulses       */
         short* ix,                          /* (o) position of pulse 1                */
         short* iy,                          /* (o) position of pulse 2                */
         short* dn,                          /* (i) corr. between target and h[]       */
         short* dn2,                         /* (i) vector of selected positions       */
         short* cor_x,                       /* (i) corr. of pulse 1 with fixed pulses */
         short* cor_y,                       /* (i) corr. of pulse 2 with fixed pulses */
         short* rrixiy                       /* (i) corr. of pulse 1 with pulse 2   */
)
    {
        short NB_PULSE_MAX = 24;
        short L_SUBFR = 64;
        short NB_TRACK = 4;
        short STEP = 4;
        short NB_POS = 16;
        short MSIZE = 256;
        short NB_MAX = 8;
        short NPMAXPT = (short) ((NB_PULSE_MAX + NB_TRACK - 1) / NB_TRACK);

        short x, y, pos, thres_ix;
        short ps1, ps2, sq, sqk;
        short alp_16, alpk;
        short* p0;
        short* p1;
        short* p2;
        int s, alp0, alp1, alp2;

        p0 = cor_x;
        p1 = cor_y;
        //p2 = rrixiy[track_x]; move16();
        p2 = &rrixiy[track_x * MSIZE];

        thres_ix = sub(nb_pos_ix, (short)NB_MAX);

        alp0 = L_deposit_h(*alp);
        alp0 = L_add(alp0, 0x00008000);       /* for rounding */

        sqk = -1;
        alpk = 1;

        *ix = track_x;
        *iy = track_y;

        for (x = track_x; x < L_SUBFR; x += STEP)
        {
            ps1 = add(*ps, dn[x]);
            alp1 = L_mac(alp0, *p0++, 4096);

            if (sub(dn2[x], thres_ix) < 0)
            {
                pos = -1;
                for (y = track_y; y < L_SUBFR; y += STEP)
                {
                    ps2 = add(ps1, dn[y]);
                    alp2 = L_mac(alp1, *p1++, 4096);
                    alp2 = L_mac(alp2, *p2++, 8192);
                    alp_16 = extract_h(alp2);

                    sq = mult(ps2, ps2);

                    s = L_msu(L_mult(alpk, sq), sqk, alp_16);

                    if (s > 0)
                    {
                        sqk = sq;
                        alpk = alp_16;
                        pos = y;
                    }
                }
                p1 -= NB_POS;

                if (pos >= 0)
                {
                    *ix = x;
                    *iy = pos;
                }
            }
            else
            {
                p2 += NB_POS;
            }
        }

        *ps = add(*ps, add(dn[*ix], dn[*iy]));
        *alp = alpk;

        return;
    }
}