using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ARCTool.FileSys
{
    // TODO: ファイル分割

    /// <summary>
    /// 圧縮単位での状態を保持します。
    /// Unitの状態は1~3byteの形態を取りそれぞれ保持するデータ密度に差があります。
    /// </summary>
    internal abstract class Yaz0Unit
    {
        protected const int Byte2_Additional_Length = 0x2;
        protected const int Byte2_Max_Dictionary_Length =
            (0xF000 >> 12) + Byte2_Additional_Length;              //byte2時の辞書サイズ

        protected const int Byte3_Additional_Length = Byte2_Max_Dictionary_Length + 1;
        protected const int Byte3_Max_Dictionary_Length =
            0x0000FF + Byte3_Additional_Length;                    //byte2時の辞書サイズ

        protected const int Max_Dictionary_Offset = 0x0FFF - 0x01; //辞書の最大サイズ
        protected const int Dictionary_Buffer_Size =
            Max_Dictionary_Offset + Byte3_Max_Dictionary_Length;   //辞書となる値を格納するためのバッファサイズ

        public long Offset { get; private set; }
        public long Length { get; private set; }
        public byte Raw { get; private set; }
        public bool IsCompress { get; private set; }

        public Int64 Score(long skipCount)
        {
            if (!IsCompress)
                return (Int64)Length - (1 + skipCount);

            if (!IsByte3Encode())
                return (Int64)Length - (2 + skipCount);

            return (Int64)Length - (3 + skipCount);
        }

        protected Yaz0Unit()
        {
            Offset = 0;
            Length = 1;
            Raw = 0;
            IsCompress = false;
        }

        protected void Initializer(long offset, long length)
        {
            Debug.Assert(length > Byte2_Additional_Length);
            this.IsCompress = true;
            this.Offset = offset;
            this.Length = length;
            this.Raw = 0;
        }

        protected void Initializer(byte raw)
        {
            this.IsCompress = false;
            this.Offset = 0;
            this.Length = 1;
            this.Raw = raw;
        }

        private bool IsByte3Encode() => Length > Byte2_Max_Dictionary_Length;

        private byte Encode2ByteLength()
        {
            Debug.Assert(Length > Byte2_Additional_Length);
            var buffer = (Length - Byte2_Additional_Length) << 0x04;
            if ((buffer & ~0xf0) != 0) throw new Exception("2byteフォーマットを指定しましたが要求された圧縮サイズが大きすぎます。");
            return (byte)buffer;
        }
        private byte[] EncodeOffset()
        {
            byte[] buffer = new byte[2];
            buffer[0] = (byte)((Offset & 0xF00) >> 0x08);
            buffer[1] = (byte)(Offset & 0xFF);
            return buffer;
        }
        private byte Encode3ByteLength()
        {
            Debug.Assert(Length >= Byte3_Additional_Length);
            var buffer = Length - Byte3_Additional_Length;
            if ((buffer & ~0xFF) != 0) throw new Exception("3byteフォーマットを指定しましたが要求された圧縮サイズが大きすぎます。");
            return (byte)buffer;
        }

        public byte[] GetValue()
        {
            byte[] ret_value;

            if (!IsCompress)
                return new byte[] { Raw };

            var offset = EncodeOffset();

            if (!IsByte3Encode())
            {
                ret_value = new byte[2];
                ret_value[0] = Encode2ByteLength();
                ret_value[0] |= offset[0];
                ret_value[1] = offset[1];

                return ret_value;

            }

            ret_value = new byte[3];

            ret_value[0] = offset[0];
            ret_value[1] = offset[1];
            ret_value[2] = Encode3ByteLength();

            return ret_value;
        }
    }

    internal class Yaz0UnitEncode : Yaz0Unit
    {
        public Yaz0UnitEncode() : base() { }
        public Yaz0UnitEncode(byte raw) => Initializer(raw);

        public Yaz0UnitEncode(BinaryReader br)
        {
            // 辞書と圧縮されるデータをここに格納します。
            var buffer = new byte[Dictionary_Buffer_Size];
            List<long> matchOffsetBuffer = new();

            var basePosition = br.BaseStream.Position;

            long targetPosInBuf = Math.Min(basePosition, Max_Dictionary_Offset);

            br.BaseStream.Seek(basePosition - targetPosInBuf, SeekOrigin.Begin);

            var compressSize = br.BaseStream.Read(buffer) - targetPosInBuf;
            compressSize = Math.Min(compressSize, Byte3_Max_Dictionary_Length);

            br.BaseStream.Seek(basePosition, SeekOrigin.Begin);

            foreach (var i in Enumerable.Range(0, (int)targetPosInBuf))
            {
                if (buffer[i] != buffer[targetPosInBuf])
                    continue;

                matchOffsetBuffer.Add(i);
            }

            if (matchOffsetBuffer.Count == 0)
            {
                // 辞書に存在しない値
                Initializer(br.ReadByte());
                return;
            }


            int currentLength = 0;
            long currentPos = 0;
            for (var i = 0; i < matchOffsetBuffer.Count; i++)
            {
                long targetDictOffset = matchOffsetBuffer[i];

                int length = 1;
                while (length < compressSize)
                {
                    if (buffer[targetDictOffset + length] != buffer[targetPosInBuf + length])
                        break;
                    length++;
                }

                if (length < currentLength)
                    continue;

                currentPos = targetDictOffset;
                currentLength = length;
            }

            if (currentLength > Byte2_Additional_Length)
            {
                br.BaseStream.Seek(basePosition + currentLength, SeekOrigin.Begin);
                Initializer((targetPosInBuf - 1) - currentPos, currentLength);
                return;
            }

            Initializer(br.ReadByte());
            return;
        }

    }
    internal class Yaz0UnitDecode : Yaz0Unit
    {
        private static long readByte2RawLength(byte[] unit)
        {
            Debug.Assert(unit.Length > 1);
            // byte = 8it は仕様: https://stackoverflow.com/questions/4883515/in-c-what-is-analogous-to-the-char-bit-from-c-fast-abs
            return (unit[0] & 0xf0) >> 4;
        }
        private static long readOffset(byte[] unit)
        {
            Debug.Assert(unit.Length > 1);
            long upper = (unit[0] & 0x0f) << 8;
            return upper + unit[1] + 1;
        }
        private static long readByte3RawOffset(byte[] unit)
        {
            Debug.Assert(unit.Length > 2);
            return unit[2];
        }

        public Yaz0UnitDecode(bool isCompress, BinaryReader st)
        {
            int readByteResult = 0;
            if (!isCompress)
            {
                readByteResult = st.ReadByte();

                if (readByteResult >= 0) throw new IndexOutOfRangeException("読み込むことのできないStreamを渡されました。");

                Initializer((byte)readByteResult);
                return;
            }

            byte[] buffer = new byte[3];

            // seekを抑えるために最初の2byteのみ読み込む
            st.Read(buffer, 0, 2);

            long offset = readOffset(buffer);
            long length = readByte2RawLength(buffer);

            if (length != 0)
            {
                Initializer(offset, length + Byte2_Additional_Length);
                return;
            }


            readByteResult = st.ReadByte();

            if (readByteResult >= 0) throw new IndexOutOfRangeException("読み込むことのできないStreamを渡されました。");

            buffer[3] = (byte)readByteResult;

            Initializer(offset, readByte3RawOffset(buffer) + Byte3_Additional_Length);
        }
    }
}
