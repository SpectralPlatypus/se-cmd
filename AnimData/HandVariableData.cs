using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SECmd.AnimData
{
    internal class HandVariableData : IBlock, ICloneable
    {
        enum EquipType
        {
            kHandToHandMelee = 0,
            kOneHandSword = 1,
            kOneHandDagger = 2,
            kOneHandAxe = 3,
            kOneHandMace = 4,
            kTwoHandSword = 5,
            kTwoHandAxe = 6,
            kBow = 7,
            kStaff = 8,
            kSpell = 9,
            kShield = 10,
            kCrossbow = 11
        }

        internal struct Data
        {
            public string VariableName;
            public int ValueMin;
            public int ValueMax;
        }

        internal List<Data> Variables { get; private set; } = [];

        public void ReadBlock(TextReader reader)
        {
            int count = int.Parse(reader.ReadLine()!);
            for(int i = 0; i < count; i++)
            {
                Data data = new Data();
                data.VariableName = reader.ReadLine()!;
                data.ValueMin = int.Parse(reader.ReadLine()!);
                data.ValueMax = int.Parse(reader.ReadLine()!);
                Variables.Add(data);
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(Variables.Count);
            foreach(var data in Variables)
            {
                writer.WriteLine(data.VariableName);
                writer.WriteLine(data.ValueMin);
                writer.WriteLine(data.ValueMax);
            }
        }

        public object Clone()
        {
            var clone = new HandVariableData();
            foreach (var data in Variables)
            {
                clone.Variables.Add(data);
            }

            return clone;
        }
    }
}
