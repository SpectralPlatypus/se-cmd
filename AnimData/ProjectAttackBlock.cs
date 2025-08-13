using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable

namespace SECmd.AnimData
{
    internal class ProjectAttackBlock : IBlock
    {
        string animVersion = "V3";
        public List<string> SwapEvents { get; private set; } = [];
        internal HandVariableData HandVariableData { get; set; } = new();
        internal ClipAttackBlock ClipAttack { get; set; } = new();

        ClipFilesCRCBlock crcData = new();

        public bool HasHandVariableData() => HandVariableData.Variables.Count > 0;
        public void ReadBlock(TextReader reader)
        {
            animVersion = reader.ReadLine();
            int count = int.Parse(reader.ReadLine());
            for (int i = 0; i < count; i++)
            {
                SwapEvents.Add(reader.ReadLine());
            }
            HandVariableData.ReadBlock(reader);
            ClipAttack.ReadBlock(reader);
            crcData.ReadBlock(reader);
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(animVersion);
            writer.WriteLine(SwapEvents.Count);
            foreach(var swapEvent in SwapEvents)
                writer.WriteLine(swapEvent);

            HandVariableData.WriteBlock(writer);
            ClipAttack.WriteBlock(writer);
            crcData.WriteBlock(writer);
        }
    }
}
