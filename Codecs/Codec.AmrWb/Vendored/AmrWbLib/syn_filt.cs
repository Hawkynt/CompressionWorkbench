/*-------------------------------------------------------------------*
 *                         SYN_FILT.C                 *
 *-------------------------------------------------------------------*
 * Do the synthesis filtering 1/A(z).                   *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private unsafe void Syn_filt(
         short* a,                           /* (i) Q12 : a[m+1] prediction coefficients           */
         short m,                             /* (i)     : order of LP filter                       */
         short* x,                           /* (i)     : input signal                             */
         short* y,                           /* (o)     : output signal                            */
         short lg,                            /* (i)     : size of filtering                        */
         short* mem,                         /* (i/o)   : memory associated with this filtering.   */
         short update)                         /* (i)     : 0=no update, 1=update of memory.         */
    {
        // 8 May 25 PHR
        short i, j;
        short* y_buf = stackalloc short[L_SUBFR16k + M16k];
        short a0, s;
        int L_tmp;
        short* yy;

        yy = &y_buf[0];

        /* copy initial filter states into synthesis buffer */
        for (i = 0; i < m; i++)
        {
            *yy++ = mem[i];
        }

        s = sub(norm_s(a[0]), 2);
        a0 = shr(a[0], 1);                     /* input / 2 */

        /* Do the filtering. */

        for (i = 0; i < lg; i++)
        {
            L_tmp = L_mult(x[i], a0);

            for (j = 1; j <= m; j++)
                L_tmp = L_msu(L_tmp, a[j], yy[i - j]);

            L_tmp = L_shl(L_tmp, add(3, s));

            //y[i] = yy[i] = round(L_tmp);
            yy[i] = round(L_tmp);   // 10 May 25 PHR
            y[i] = yy[i];
        }

        /* Update memory if required */
        if (update != 0)
        {
            for (i = 0; i < m; i++)
            {
                mem[i] = yy[lg - m + i];
            }
        }

        return;
    }

    // 30 Apr 24 PHR
    private unsafe void Syn_filt(
         short* a,                           /* (i) Q12 : a[m+1] prediction coefficients           */
         short m,                             /* (i)     : order of LP filter                       */
         short* x,                           /* (i)     : input signal                             */
         short* y,                           /* (o)     : output signal                            */
         short lg,                            /* (i)     : size of filtering                        */
         short[] mem,                         /* (i/o)   : memory associated with this filtering.   */
         short update)                         /* (i)     : 0=no update, 1=update of memory.         */
    {
        // 8 May 25 PHR
        short i, j;
        short* y_buf = stackalloc short[L_SUBFR16k + M16k];
        short a0, s;
        int L_tmp;
        short* yy;

        yy = &y_buf[0];

        /* copy initial filter states into synthesis buffer */
        for (i = 0; i < m; i++)
        {
            *yy++ = mem[i];
        }

        s = sub(norm_s(a[0]), 2);
        a0 = shr(a[0], 1);                     /* input / 2 */

        /* Do the filtering. */

        for (i = 0; i < lg; i++)
        {
            L_tmp = L_mult(x[i], a0);

            for (j = 1; j <= m; j++)
                L_tmp = L_msu(L_tmp, a[j], yy[i - j]);

            L_tmp = L_shl(L_tmp, add(3, s));

            //y[i] = yy[i] = round(L_tmp);
            yy[i] = round(L_tmp);   // 20 May 25 PHR
            y[i] = yy[i];
        }

        /* Update memory if required */
        if (update != 0)
        {
            for (i = 0; i < m; i++)
            {
                mem[i] = yy[lg - m + i];
            }
        }

        return;
    }

    // 12 May 25 PHR
    private unsafe void Syn_filt(
         short* a,                           /* (i) Q12 : a[m+1] prediction coefficients           */
         short m,                             /* (i)     : order of LP filter                       */
         short* x,                           /* (i)     : input signal                             */
         short* y,                           /* (o)     : output signal                            */
         short lg,                            /* (i)     : size of filtering                        */
         short[] mem,                         /* (i/o)   : memory associated with this filtering.   */
         int mem_index,                      // Index into mem[]
         short update)                         /* (i)     : 0=no update, 1=update of memory.         */
    {
        // 8 May 25 PHR
        short i, j;
        short* y_buf = stackalloc short[L_SUBFR16k + M16k];
        short a0, s;
        int L_tmp;
        short* yy;

        yy = &y_buf[0];

        /* copy initial filter states into synthesis buffer */
        for (i = 0; i < m; i++)
        {
            *yy++ = mem[i + mem_index];
        }

        s = sub(norm_s(a[0]), 2);
        a0 = shr(a[0], 1);                     /* input / 2 */

        /* Do the filtering. */

        for (i = 0; i < lg; i++)
        {
            L_tmp = L_mult(x[i], a0);

            for (j = 1; j <= m; j++)
                L_tmp = L_msu(L_tmp, a[j], yy[i - j]);

            L_tmp = L_shl(L_tmp, add(3, s));

            //y[i] = yy[i] = round(L_tmp);
            yy[i] = round(L_tmp);   // 20 May 25 PHR
            y[i] = yy[i];
        }

        /* Update memory if required */
        if (update != 0)
        {
            for (i = 0; i < m; i++)
            {
                mem[i + mem_index] = yy[lg - m + i];
            }
        }

        return;
    }


    private unsafe void Syn_filt_32(
         short* a,                           /* (i) Q12 : a[m+1] prediction coefficients */
         short m,                             /* (i)     : order of LP filter             */
         short* exc,                         /* (i) Qnew: excitation (exc[i] >> Qnew)    */
         short Qnew,                          /* (i)     : exc scaling = 0(min) to 8(max) */
         short* sig_hi,                      /* (o) /16 : synthesis high                 */
         short* sig_lo,                      /* (o) /16 : synthesis low                  */
         short lg                             /* (i)     : size of filtering              */
    )
    {
        short i, j, a0, s;
        int L_tmp;

        s = sub(norm_s(a[0]), 2);

        a0 = shr(a[0], add(4, Qnew));          /* input / 16 and >>Qnew */

        /* Do the filtering. */

        for (i = 0; i < lg; i++)
        {
            L_tmp = 0;
            for (j = 1; j <= m; j++)
                L_tmp = L_msu(L_tmp, sig_lo[i - j], a[j]);

            L_tmp = L_shr(L_tmp, 16 - 4);      /* -4 : sig_lo[i] << 4 */

            L_tmp = L_mac(L_tmp, exc[i], a0);

            for (j = 1; j <= m; j++)
                L_tmp = L_msu(L_tmp, sig_hi[i - j], a[j]);

            /* sig_hi = bit16 to bit31 of synthesis */
            L_tmp = L_shl(L_tmp, add(3, s));           /* ai in Q12 */
            sig_hi[i] = extract_h(L_tmp);

            /* sig_lo = bit4 to bit15 of synthesis */
            L_tmp = L_shr(L_tmp, 4);           /* 4 : sig_lo[i] >> 4 */
            sig_lo[i] = extract_l(L_msu(L_tmp, sig_hi[i], 2048));
        }

        return;
    }

}
