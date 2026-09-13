using System;
internal static class BaselineHash {
    internal static unsafe ulong ComputeSourceHash(IntPtr dibBits, int srcW, int srcH)
    {
        if (dibBits == IntPtr.Zero || srcW <= 0 || srcH <= 0) return 0;

        const ulong fnvPrime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        byte* src = (byte*)dibBits;
        int rowBytes = srcW * 4;
        int qwordsPerRow = rowBytes >> 3;
        int qwordStep = Math.Max(1, qwordsPerRow / 96);
        int rowStep = Math.Max(1, srcH / 96);

        for (int y = 0; y < srcH; y += rowStep)
        {
            byte* rowStart = src + (long)y * rowBytes;
            ulong* row = (ulong*)rowStart;
            for (int i = 0; i < qwordsPerRow; i += qwordStep)
            {
                hash ^= row[i];
                hash *= fnvPrime;
            }
        }

        return hash;
    }

}
