using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SECmd.AnimData
{
    internal class ClipGeneratorBlock : IBlock,ILineCounter
    {
        public string Name { get; private set; } = "";
        public int CacheIndex { get; private set; } = 0;
        float playbackSpeed = 0f;
        float cropStartTime = 0f; 
        float cropEndTime = 0f; 
        List<(string, float)> events = [];

        public int LineCount { get { return 6 +  events.Count + 1; } }



        public void ReadBlock(TextReader reader)
        {
            Name = reader.ReadLine()!;
            CacheIndex = int.Parse(reader.ReadLine()!);
            playbackSpeed = float.Parse(reader.ReadLine()!);
            cropStartTime = float.Parse(reader.ReadLine()!);
            cropEndTime = float.Parse(reader.ReadLine()!);
            
            int eventCount = int.Parse(reader.ReadLine()!);
            for (int i = 0; i < eventCount; i++)
            {
                var line = reader.ReadLine();
                var entries = line?.Split(':');
                if (entries?.Length != 2) throw new IOException("Unexpected event format!");
                events.Add((entries[0], float.Parse(entries[1])));
            }
            reader.ReadLine();
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(Name);
            writer.WriteLine(CacheIndex);
            writer.WriteLine(playbackSpeed);
            writer.WriteLine(cropStartTime);
            writer.WriteLine(cropEndTime);
            writer.WriteLine(events.Count);
            foreach (var item in events)
            {
                writer.WriteLine(item.Item1 + ":" + item.Item2);
            }
            writer.WriteLine();
        }
    }
}
