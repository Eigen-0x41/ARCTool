using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace ARCTool.FileSys
{
    // TODO: ファイル分割

    internal abstract class Yaz0Chunk
    {
        protected const int Unit_Size = 8;
        protected const long Flag_Mask_First = 0b1000_0000;

        private Yaz0Unit[] units;

        protected void Initializer(Yaz0Unit[] units)
        {
            Debug.Assert(units.Length == Unit_Size);
            this.units = units;
        }

        public byte[] GetValue()
        {
            List<byte> retValue = new();
            retValue.Add(0x00);


            foreach (var i in Enumerable.Range(0, Unit_Size))
            {
                retValue.AddRange(units[i].GetValue());

                if (units[i].IsCompress)
                    continue;
                retValue[0] |= (byte)(Flag_Mask_First >> i);
            }

            return retValue.ToArray();
        }
    }

    internal class Yaz0ChunkRawEncode : Yaz0Chunk
    {
        public Yaz0ChunkRawEncode(BinaryReader st)
        {
            var units = new Yaz0Unit[Unit_Size];


            foreach (var i in Enumerable.Range(0, Unit_Size))
            {
                if (st.BaseStream.Position < st.BaseStream.Length)
                {
                    units[i] = new Yaz0UnitEncode(st.ReadByte());
                }
                else
                {
                    units[i] = new Yaz0UnitEncode();
                }
            }


            Initializer(units);
        }
    }

    internal class Yaz0ChunkEncode : Yaz0Chunk
    {
        public static List<Yaz0Unit> PreprocessUnit(BinaryReader st, in List<Yaz0Unit> beforUnits)
        {
            List<Yaz0Unit> retValue = new(beforUnits);
            Yaz0Unit bufUnit = null;
            long skipCount = 0;

            for (var basePos = st.BaseStream.Position;
                (retValue.Count < Unit_Size) && (basePos < st.BaseStream.Length);
                basePos = st.BaseStream.Position)
            {
                if (skipCount > 0)
                {
                    retValue.Add(bufUnit);
                }
                else
                {
                    retValue.Add(new Yaz0UnitEncode(st));
                }
                st.BaseStream.Seek(basePos + 1, SeekOrigin.Begin);
                bufUnit = new Yaz0UnitEncode(st);

                if (bufUnit.Score(skipCount + 1) > retValue.Last().Score(skipCount))
                {
                    skipCount++;
                    st.BaseStream.Seek(basePos, SeekOrigin.Begin);
                    retValue[^1] = new Yaz0UnitEncode(st.ReadByte());
                    continue;
                }

                st.BaseStream.Seek(basePos + retValue.Last().Length, SeekOrigin.Begin);
                skipCount = 0;
                continue;
            }

            return retValue;
        }

        public Yaz0ChunkEncode(BinaryReader st)
        {
            var units = new Yaz0Unit[Unit_Size];


            foreach (var i in Enumerable.Range(0, Unit_Size))
            {
                if (st.BaseStream.Position < st.BaseStream.Length)
                {
                    units[i] = new Yaz0UnitEncode(st);
                    continue;
                }
                units[i] = new Yaz0UnitEncode();
            }


            Initializer(units);
        }

        public Yaz0ChunkEncode(ref List<Yaz0Unit> units)
        {
            var buf = new Yaz0Unit[Unit_Size];
            foreach (var i in Enumerable.Range(0, Unit_Size))
            {
                if (i < units.Count())
                {
                    buf[i] = units[i];
                    continue;
                }
                buf[i] = new Yaz0UnitEncode();
            }
            Initializer(buf);

            units = new(units.ToArray()[Math.Min(Unit_Size, units.Count())..]);
        }
    }

    internal class Yaz0ChunkDecode : Yaz0Chunk
    {
        public Yaz0ChunkDecode(BinaryReader st)
        {
            byte flagResult = st.ReadByte();
            bool[] flags = new bool[Unit_Size];

            foreach (var i in Enumerable.Range(0, Unit_Size))
            {
                flags[i] = (flagResult & (Flag_Mask_First >> i)) != 0;
            }

            var units = new Yaz0Unit[Unit_Size];

            foreach (var i in Enumerable.Range(0, Unit_Size))
            {
                units[i] = new Yaz0UnitDecode(flags[i], st);
            }

            Initializer(units);
        }
    }
}
