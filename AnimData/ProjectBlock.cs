namespace SECmd.AnimData
{
    internal class ProjectBlock : IBlock, ILineCounter
    {
        bool hasProjectFiles = false;
        List<string> projectFiles = [];
        bool hasAnimationCache = false;
        public bool HasAnimationCache { get { return hasAnimationCache; } }
        public List<ClipGeneratorBlock> Clips { get; } = [];

        public int LineCount
        {
            get
            {
                return 2 + projectFiles.Count + 1 + Clips.Aggregate(0, (acc, x) => acc += x.LineCount);
            }
        }

        public void ReadBlock(TextReader reader)
        {
            projectFiles.Clear();
            Clips.Clear();

            string input = reader.ReadLine()!;
            hasProjectFiles = input == "1";
            if (hasProjectFiles)
            {
                int count = int.Parse(reader.ReadLine()!);
                for (int i = 0; i < count; i++)
                {
                    projectFiles.Add(reader.ReadLine()!);
                }
            }
            input = reader.ReadLine()!;
            hasAnimationCache = input == "1";
            if (hasAnimationCache)
            {
                while(reader.Peek() >= 0)
                {
                    ClipGeneratorBlock clipBlock = new();
                    clipBlock.ReadBlock(reader);
                    Clips.Add(clipBlock);
                }
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(hasProjectFiles ? "1" : "0");
            if(hasProjectFiles)
            {
                writer.WriteLine(projectFiles.Count);
                foreach (string file in projectFiles)
                    writer.WriteLine(file);
            }
            writer.WriteLine(hasAnimationCache ? "1" : "0");
            if (hasAnimationCache)
            {
                foreach(var clipBlock in Clips)
                {
                    clipBlock.WriteBlock(writer);
                }
            }
        }
    }
}
