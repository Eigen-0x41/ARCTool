using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARCTool.FileSys
{
    internal unsafe class Yaz0Encode
    {
        private const int Byte2_Additional_Length = 0x2;
        private const int Byte2_Max_Dictionary_Length =
            (0xF000 >> 12) + Byte2_Additional_Length;              //byte2時の辞書サイズ

        private const int Byte3_Additional_Length = Byte2_Max_Dictionary_Length + 1;
        private const int Byte3_Max_Dictionary_Length =
            0x0000FF + Byte3_Additional_Length;                    //byte2時の辞書サイズ

        private const int Max_Dictionary_Offset = 0x0FFF; //辞書の最大サイズ

        unsafe
        private class Unit
        {
            public long Length;
            public long Offset;

            private long CompressSize()
            {
                if (Length >= Byte3_Additional_Length)
                    return 0x03;

                if (Length > Byte2_Additional_Length)
                    return 0x02;

                return 0x01;
            }

            public long Score(long diffPos)
            {
                Debug.Assert(Length > 0);
                return Length - CompressSize() - diffPos;
            }

            public void Init()
            {
                // Lengthが1となっていますが、
                // その場合はraw値が書き込まれるため
                // バイナリへの影響はありません。
                Length = 1;
                Offset = 0;
            }

            public long Write(int* flag, int writeFlagCount, byte* dst, long* pos, byte* src, in long srcPos)
            {
                if (Length > Byte2_Additional_Length)
                {   // 2byte or 3byte
                    dst[*pos + 0x01] = (byte)(0xFF & Offset);

                    long length = 0;
                    if (Length >= Byte3_Additional_Length)
                    {   // 3byte
                        dst[*pos] = (byte)(0x0F & (Offset >> 0x08));
                        length = Length - Byte3_Additional_Length;
                        dst[*pos + 0x02] = (byte)length;
                        *pos += 0x03;
                        return Length;
                    }

                    length = Length - Byte2_Additional_Length;
                    dst[*pos] = (byte)((0xF0 & (length << 0x04)) |
                                      (0x0F & (Offset >> 0x08)));
                    *pos += 0x02;
                    return Length;
                }

                *flag |= 0b1000_0000 >> writeFlagCount;
                dst[*pos] = src[srcPos];
                *pos += 1;
                return 0x01;
            }
        }

        private unsafe void GenUnit(Unit dst, in byte* data, in long size, in long pos)
        {
            dst.Init();
            long maxCompSize = (size - pos < Byte3_Max_Dictionary_Length)
                                   ? size - pos
                                   : Byte3_Max_Dictionary_Length;

            for (long dictPos = (pos > Max_Dictionary_Offset)
                                    ? pos - Max_Dictionary_Offset + 1
                                    : 0;
                dictPos < pos;
                dictPos++)
            {
                long length = 0;
                while (length < maxCompSize)
                {
                    if (data[dictPos + length] != data[pos + length])
                        break;
                    length++;
                }

                if (length <= dst.Length)
                    continue;

                dst.Length = length;
                dst.Offset = dictPos;
            }

            Debug.Assert(dst.Length > 0);

            dst.Offset = (pos - 1) - dst.Offset;
        }

        // varを使用するとその際にコストがかかるかもしれないので
        // この時点で宣言しておく。
        private Unit bufferForSwap = null;

        private unsafe long Encode(byte* dst, in byte* src, in long size)
        {
            long dstPos = 0;
            long srcPos = 0;
            // 先頭の値は8バイト圧縮され"ます"(yaz0enc.exeより)。

            int flag = 0x00;
            long flagPos = dstPos;
            int writeFlagCount = 7;

            Unit currentUnit = new();
            Unit nextUnit = new();
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
                    // 参照先の交換
                    // やっていることはポインタの交換と同じ。
                    bufferForSwap = currentUnit;
                    currentUnit = nextUnit;
                    nextUnit = bufferForSwap;
                }
                else
                {
                    GenUnit(currentUnit, src, size, srcPos);
                }

                GenUnit(nextUnit, src, size, srcPos + 1);

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
            var src = new byte[br.BaseStream.Length];
            var dst = new byte[br.BaseStream.Length];
            br.Read(src);

            long dstSize = 0;

            fixed (byte* pDst = dst, pSrc = src)
                dstSize = Encode(pDst, pSrc, br.BaseStream.Length);

            bw.Write(dst, 0, (int)dstSize);
        }
    }
}
