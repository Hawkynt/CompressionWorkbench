/*-----------------------------------------------------------------------*
 *                         ISP_AZ.C                     *
 *-----------------------------------------------------------------------*
 * Compute the LPC coefficients from isp (order=M)             *
 *-----------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    //private const int NC = (M / 2);
    private const int NC16k = (M16k / 2);

    private unsafe void Isp_Az(
         short[] isp,                        /* (i) Q15 : Immittance spectral pairs            */
         short* a,                           /* (o) Q12 : predictor coefficients (order = M)   */
         short m,
         short adaptive_scaling)             /* (i) 0   : adaptive scaling disabled */
                                             /*     1   : adaptive scaling enabled  */
    {
        short i, j, hi, lo;
        int* f1 = stackalloc int[NC16k + 1];
        int* f2 = stackalloc int[NC16k];
        short nc;
        int t0;
        short q, q_sug;
        int tmax;

        // 5 May 25 PHR
        short* isp_stack = stackalloc short[isp.Length];
        Copy(isp, isp_stack, (short)isp.Length);

        nc = shr(m, 1);
        if (sub(nc, 8) > 0)
        {
            Get_isp_pol_16kHz(isp_stack, f1, nc);
            for (i = 0; i <= nc; i++)
            {
                f1[i] = L_shl(f1[i], 2);
            }
        }
        else
            Get_isp_pol(isp_stack, f1, nc);

        if (sub(nc, 8) > 0)
        {
            Get_isp_pol_16kHz(&isp_stack[1], f2, sub(nc, 1));
            for (i = 0; i <= nc - 1; i++)
            {
                f2[i] = L_shl(f2[i], 2);
            }
        }
        else
            Get_isp_pol(&isp_stack[1], f2, sub(nc, 1));

        /*-----------------------------------------------------*
         *  Multiply F2(z) by (1 - z^-2)                       *
         *-----------------------------------------------------*/

        for (i = sub(nc, 1); i > 1; i--)
        {
            f2[i] = L_sub(f2[i], f2[i - 2]);  /* f2[i] -= f2[i-2]; */
        }

        /*----------------------------------------------------------*
         *  Scale F1(z) by (1+isp[m-1])  and  F2(z) by (1-isp[m-1]) *
         *----------------------------------------------------------*/

        for (i = 0; i < nc; i++)
        {
            /* f1[i] *= (1.0 + isp[M-1]); */

            hi = 0;
            lo = 0;
            L_Extract(f1[i], ref hi, ref lo);
            t0 = Mpy_32_16(hi, lo, isp_stack[m - 1]);
            f1[i] = L_add(f1[i], t0);

            /* f2[i] *= (1.0 - isp[M-1]); */

            L_Extract(f2[i], ref hi, ref lo);
            t0 = Mpy_32_16(hi, lo, isp_stack[m - 1]);
            f2[i] = L_sub(f2[i], t0);
        }

        /*-----------------------------------------------------*
         *  A(z) = (F1(z)+F2(z))/2                             *
         *  F1(z) is symmetric and F2(z) is antisymmetric      *
         *-----------------------------------------------------*/

        /* a[0] = 1.0; */
        a[0] = 4096;
        tmax = 1;
        for (i = 1, j = sub(m, 1); i < nc; i++, j--)
        {
            /* a[i] = 0.5*(f1[i] + f2[i]); */

            t0 = L_add(f1[i], f2[i]);          /* f1[i] + f2[i]             */
            tmax |= L_abs(t0);
            a[i] = extract_l(L_shr_r(t0, 12)); /* from Q23 to Q12 and * 0.5 */

            /* a[j] = 0.5*(f1[i] - f2[i]); */

            t0 = L_sub(f1[i], f2[i]);          /* f1[i] - f2[i]             */
            tmax |= L_abs(t0);
            a[j] = extract_l(L_shr_r(t0, 12)); /* from Q23 to Q12 and * 0.5 */
        }

        /* rescale data if overflow has occured and reprocess the loop */

        if (sub(adaptive_scaling, 1) == 0)
            q = sub(4, norm_l(tmax));        /* adaptive scaling enabled */
        else
            q = 0;       /* adaptive scaling disabled */

        if (q > 0)
        {
            q_sug = add(12, q);
            for (i = 1, j = sub(m, 1); i < nc; i++, j--)
            {
                /* a[i] = 0.5*(f1[i] + f2[i]); */

                t0 = L_add(f1[i], f2[i]);          /* f1[i] + f2[i]             */
                a[i] = extract_l(L_shr_r(t0, q_sug)); /* from Q23 to Q12 and * 0.5 */

                /* a[j] = 0.5*(f1[i] - f2[i]); */

                t0 = L_sub(f1[i], f2[i]);          /* f1[i] - f2[i]             */
                a[j] = extract_l(L_shr_r(t0, q_sug)); /* from Q23 to Q12 and * 0.5 */
            }
            a[0] = shr(a[0], q);
        }
        else
        {
            q_sug = 12;
            q = 0;
        }


        /* a[NC] = 0.5*f1[NC]*(1.0 + isp[M-1]); */
        hi = 0;
        lo = 0;
        L_Extract(f1[nc], ref hi, ref lo);
        t0 = Mpy_32_16(hi, lo, isp_stack[m - 1]);
        t0 = L_add(f1[nc], t0);
        a[nc] = extract_l(L_shr_r(t0, q_sug));    /* from Q23 to Q12 and * 0.5 */
        
        /* a[m] = isp[m-1]; */
        a[m] = shr_r(isp_stack[m - 1], add(3, q));           /* from Q15 to Q12          */
    }

    /*-----------------------------------------------------------*
     * procedure Get_isp_pol:                                    *
     *           ~~~~~~~~~~~                                     *
     *   Find the polynomial F1(z) or F2(z) from the ISPs.       *
     * This is performed by expanding the product polynomials:   *
     *                                                           *
     * F1(z) =   product   ( 1 - 2 isp_i z^-1 + z^-2 )           *
     *         i=0,2,4,6,8                                       *
     * F2(z) =   product   ( 1 - 2 isp_i z^-1 + z^-2 )           *
     *         i=1,3,5,7                                         *
     *                                                           *
     * where isp_i are the ISPs in the cosine domain.            *
     *-----------------------------------------------------------*
     *                                                           *
     * Parameters:                                               *
     *  isp[]   : isp vector (cosine domaine)         in Q15     *
     *  f[]     : the coefficients of F1 or F2        in Q23     *
     *  n       : == NC for F1(z); == NC-1 for F2(z)             *
     *-----------------------------------------------------------*/

    private unsafe void Get_isp_pol(short* isp, int* f, short n)
    {
        short i, j, hi, lo;
        int t0;

        /* All computation in Q23 */

        f[0] = L_mult(4096, 1024);      /* f[0] = 1.0;        in Q23  */
        f[1] = L_mult(isp[0], -256);    /* f[1] = -2.0*isp[0] in Q23  */

        f += 2;     /* Advance f pointer          */
        isp += 2;   /* Advance isp pointer        */

        hi = 0;
        lo = 0;
        for (i = 2; i <= n; i++)
        {
            *f = f[-2];

            for (j = 1; j < i; j++, f--)
            {
                L_Extract(f[-1], ref hi, ref lo);
                t0 = Mpy_32_16(hi, lo, *isp);  /* t0 = f[-1] * isp    */
                t0 = L_shl(t0, 1);
                *f = L_sub(*f, t0);     /* *f -= t0            */
                *f = L_add(*f, f[-2]);  /* *f += f[-2]         */
            }
            *f = L_msu(*f, *isp, 256);  /* *f -= isp<<8        */
            f += i;                     /* Advance f pointer   */
            isp += 2;                   /* Advance isp pointer */
        }
        return;
    }

    private unsafe void Get_isp_pol_16kHz(short* isp, int* f, short n)
    {
        short i, j, hi, lo;
        int t0;

        /* All computation in Q23 */

        f[0] = L_mult(4096, 256);       /* f[0] = 1.0;        in Q23  */
        f[1] = L_mult(isp[0], -64);     /* f[1] = -2.0*isp[0] in Q23  */

        f += 2;     /* Advance f pointer          */
        isp += 2;  /* Advance isp pointer        */

        hi = 0;
        lo = 0;

        for (i = 2; i <= n; i++)
        {
            *f = f[-2];

            for (j = 1; j < i; j++, f--)
            {
                L_Extract(f[-1], ref hi, ref lo);
                t0 = Mpy_32_16(hi, lo, *isp);  /* t0 = f[-1] * isp    */
                t0 = L_shl(t0, 1);
                *f = L_sub(*f, t0);     /* *f -= t0            */
                *f = L_add(*f, f[-2]);  /* *f += f[-2]         */
            }
            *f = L_msu(*f, *isp, 64);           /* *f -= isp<<8        */
            f += i;                            /* Advance f pointer   */
            isp += 2;                          /* Advance isp pointer */
        }
        return;
    }

}
