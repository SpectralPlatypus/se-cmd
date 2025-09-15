namespace SECmd.AnimData
{
    internal class ProjectBlock : IBlock, ILineCounter, ICloneable
    {
        bool hasProjectFiles = false;
        List<string> projectFiles = [];
        public List<ClipGeneratorBlock> Clips { get; private set; } = [];
        public bool HasAnimationCache { get; private set; } = false;

        public int LineCount
        {
            get
            {
                return 2 + projectFiles.Count + 1 + Clips.Aggregate(0, (acc, x) => acc += x.LineCount);
            }
        }

        public object Clone()
        {
            ProjectBlock clone = new()
            {
                hasProjectFiles = this.hasProjectFiles,
                HasAnimationCache = this.HasAnimationCache,
                projectFiles = [.. this.projectFiles],
                Clips = []
            };
            foreach (var x in Clips)
            {
                clone.Clips.Add((ClipGeneratorBlock)x.Clone());
            }
            return clone;
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
            HasAnimationCache = input == "1";
            if (HasAnimationCache)
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
            writer.WriteLine(HasAnimationCache ? "1" : "0");
            if (HasAnimationCache)
            {
                foreach(var clipBlock in Clips)
                {
                    clipBlock.WriteBlock(writer);
                }
            }
        }
    }
}
