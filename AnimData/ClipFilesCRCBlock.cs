#nullable disable

namespace SECmd.AnimData
{
    internal class ClipFilesCRCBlock : IBlock
    {
        List<string> entries = [];

        public ClipFilesCRCBlock(List<string> entries)
        {
            this.entries = entries;
        }

        public ClipFilesCRCBlock()
        {
        }

        public void Append(string pathCrc, string nameCrc)
        {
            entries.Add(pathCrc);
            entries.Add(nameCrc);
            entries.Add("7891816");
        }

        public void ReadBlock(TextReader reader)
        {
            int count = int.Parse(reader.ReadLine())*3;
            for (int i = 0; i < count; i++)
            {
                entries.Add(reader.ReadLine());
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            if (entries.Count % 3 != 0)
                throw new Exception("Unexpect CRC Block count");
            writer.WriteLine(entries.Count/3);
            foreach (string entry in entries)
                writer.WriteLine(entry);
        }
    }
}
