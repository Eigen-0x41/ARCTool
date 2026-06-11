using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;

internal unsafe class Yaz0Encode
{
    private const int Byte2_Additional_Length = 0x2;
    private const int Byte2_Max_Dictionary_Length = (0xF000 >> 12) + Byte2_Additional_Length;

    private const int Byte3_Additional_Length = Byte2_Max_Dictionary_Length + 1;
    private const int Byte3_Max_Dictionary_Length = 0x0000FF + Byte3_Additional_Length;

    private const int Max_Dictionary_Offset = 0x0FFF;

    // 改善点1: struct に変更してスタック上に配置。GCの負担を完全にゼロにする。
    private struct Unit
    {
        public int Length;
        public int Offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly int CompressSize()
        {
            if (Length >= Byte3_Additional_Length)
                return 0x03;

            if (Length > Byte2_Additional_Length)
                return 0x02;

            return 0x01;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int Score(int diffPos)
        {
            Debug.Assert(Length > 0);
            return Length - CompressSize() - diffPos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Init()
        {
            Length = 1;
            Offset = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Write(int* flag, int writeFlagCount, byte* dst, long* pos, byte* src, in long srcPos)
        {
            if (Length > Byte2_Additional_Length)
            {
                dst[*pos + 0x01] = (byte)(0xFF & Offset);

                int length = 0;
                if (Length >= Byte3_Additional_Length)
                {
                    dst[*pos] = (byte)(0x0F & (Offset >> 0x08));
                    length = Length - Byte3_Additional_Length;
                    dst[*pos + 0x02] = (byte)length;
                    *pos += 0x03;
                    return Length;
                }

                length = Length - Byte2_Additional_Length;
                dst[*pos] = (byte)((0xF0 & (length << 0x04)) | (0x0F & (Offset >> 0x08)));
                *pos += 0x02;
                return Length;
            }

            *flag |= 0b1000_0000 >> writeFlagCount;
            dst[*pos] = src[srcPos];
            *pos += 1;
            return 0x01;
        }
    }

    // 改善点2: ref struct を使うことで無駄なコピーを避ける
    // 改善点3: 不正なOffsetの計算バグを修正
    private unsafe void GenUnit(ref Unit dst, byte* data, long size, long pos)
    {
        dst.Init();
        if (pos >= size) return;

        long maxCompSize = (size - pos < Byte3_Max_Dictionary_Length)
                               ? size - pos
                               : Byte3_Max_Dictionary_Length;

        long startDictPos = (pos > Max_Dictionary_Offset)
                                ? pos - Max_Dictionary_Offset
                                : 0;

        int bestLength = 1;
        int bestOffset = 0;

        // 全探索ループの最適化（インクリメントとポインタ演算の効率化）
        for (long dictPos = startDictPos; dictPos < pos; dictPos++)
        {
            // 早期リジェクション：先頭文字と、現在見つかっている最大長の先の文字が一致しないならパス
            // これだけで無駄な深追いをかなり減らせる
            if (data[dictPos] != data[pos] || data[dictPos + bestLength - 1] != data[pos + bestLength - 1])
                continue;

            int length = 0;
            while (length < maxCompSize && data[dictPos + length] == data[pos + length])
            {
                length++;
            }

            if (length > bestLength)
            {
                bestLength = length;
                // ここで「相対距離」を計算して保持する（0から始まる値、1文字前なら0）
                bestOffset = (int)((pos - 1) - dictPos);

                if (length == maxCompSize)
                    break;
            }
        }

        dst.Length = bestLength;
        dst.Offset = bestOffset;
    }

    private unsafe long Encode(byte* dst, byte* src, long size)
    {
        long dstPos = 0;
        long srcPos = 0;

        int flag = 0x00;
        long flagPos = dstPos;
        int writeFlagCount = 7;

        // クラスのフィールド経由ではなく、完全にスタック上のローカル変数にする
        Unit currentUnit = default;
        Unit nextUnit = default;
        bool isNextUnitValid = false;

        while (srcPos < size)
        {
            writeFlagCount++;
            if (writeFlagCount == 8)
            {
                dst[flagPos] = (byte)flag;
                flag = 0x00;
                flagPos = dstPos;
                dstPos++;
                writeFlagCount = 0;
            }

            if (isNextUnitValid)
            {
                // 改善点4: C#の最適化されたタプル置換。
                // 内部的にはレジスタの入れ替え、あるいは構造体のシャローコピーになり、フィールドアクセスより遥かに速い。
                (currentUnit, nextUnit) = (nextUnit, currentUnit);
            }
            else
            {
                GenUnit(ref currentUnit, src, size, srcPos);
            }

            GenUnit(ref nextUnit, src, size, srcPos + 1);

            isNextUnitValid = nextUnit.Score(1) > currentUnit.Score(0);
            if (isNextUnitValid)
            {
                flag |= 0b1000_0000 >> writeFlagCount;
                dst[dstPos] = src[srcPos];
                dstPos++;
                srcPos++;
                continue;
            }

            srcPos += currentUnit.Write(&flag, writeFlagCount, dst, &dstPos, src, srcPos);
        }

        dst[flagPos] = (byte)flag;

        return dstPos;
    }

    public void Encode(BinaryWriter bw, BinaryReader br)
    {
        long srcLength = br.BaseStream.Length;
        if (srcLength == 0) return;

        var src = new byte[srcLength];
        var dst = new byte[srcLength * 2]; // 念のためのオーバーフロー防止
        br.Read(src);

        long dstSize = 0;

        fixed (byte* pDst = dst, pSrc = src)
            dstSize = Encode(pDst, pSrc, srcLength);

        bw.Write(dst, 0, (int)dstSize);
    }
}