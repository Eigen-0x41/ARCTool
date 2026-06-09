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

            public int Score(long skipCount)
            {
                Debug.Assert(Length > 0);
                return (int)Length - (int)(CompressSize() + skipCount);
            }

            public void Init()
            {
                // Lengthが1となっていますが、
                // その場合はraw値が書き込まれるため
                // バイナリへの影響はありません。
                Length = 1;
                Offset = 0;
            }

            public long Write(byte* flag, int writeFlagCount, byte* dst, in long pos, byte* src, in long srcPos)
            {
                if (Length > Byte2_Additional_Length)
                {   // 2byte or 3byte
                    dst[pos] = (byte)((0x0F00 & Offset) >> 0x08);
                    dst[pos + 1] = (byte)(0x00FF & Offset);

                    long length = 0;
                    if (Length >= Byte3_Additional_Length)
                    {   // 3byte
                        length = Length - Byte3_Additional_Length;
                        dst[pos + 2] = (byte)length;
                        return 0x03;
                    }

                    length = Length - Byte2_Additional_Length;
                    dst[pos] |= (byte)(0xF0 & (length << 0x04));
                    return 0x02;
                }

                Length = 1;
                *flag |= (byte)(0b1000_0000 >> writeFlagCount);
                dst[pos] = src[srcPos];
                return 0x01;
            }
        }

        private unsafe void GenUnit(Unit dst, in byte* data, in long size, in long pos)
        {
            dst.Init();
            long dictBeginOffset = Math.Min(pos, Max_Dictionary_Offset + 1);
            long maxCompSize = Math.Min(Byte3_Max_Dictionary_Length, size - pos);

            for (long dictPos = pos - dictBeginOffset; dictPos < pos; dictPos++)
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

        private unsafe long Encode(byte* dst, in byte* src, in long size)
        {
            long dstPos = 0;
            long srcPos = 0;
            // 先頭の値は8バイト圧縮され"ます"(yaz0enc.exeより)。

            byte flag = 0x00;
            long flagPos = dstPos;
            int writeFlagCount = 8;

            Unit currentUnit = new();
            Unit nextUnit = new();
            bool isNextUnitValid = false;
            long skipCount = 0;

            while (srcPos < size)
            {
                if (writeFlagCount == 8)
                {
                    dst[flagPos] = flag;
                    flag = 0x00;
                    flagPos = dstPos;
                    writeFlagCount = 0x00;
                    dstPos++;
                }

                if (isNextUnitValid)
                {
                    // 参照先の交換
                    // やっていることはポインタの交換と同じ。
                    var buffer = currentUnit;
                    currentUnit = nextUnit;
                    nextUnit = buffer;
                }
                else
                {
                    GenUnit(currentUnit, src, size, srcPos);
                }

                GenUnit(nextUnit, src, size, srcPos + 1);

                if (currentUnit.Score(skipCount) < nextUnit.Score(skipCount + 1))
                {
                    flag |= (byte)(0b1000_0000 >> writeFlagCount);
                    dst[dstPos] = src[srcPos];
                    dstPos++;
                    srcPos++;
                    skipCount++;
                    isNextUnitValid = true;
                }
                else
                {
                    var dstSize = currentUnit.Write(&flag, writeFlagCount, dst, dstPos, src, srcPos);
                    dstPos += dstSize;
                    srcPos += currentUnit.Length;
                    skipCount = 0;
                    isNextUnitValid = false;
                }

                writeFlagCount++;
            }

            dst[flagPos] = flag;

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
