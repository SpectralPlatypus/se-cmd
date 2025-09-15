namespace SECmd.AnimData
{
    internal struct AttackDataBlock
    {
        public string EventName = "";
        public int Mirrored = 0;
        public List<string> Clips = [];

        public AttackDataBlock()
        {
        }

        public readonly bool IsMirrored() => Mirrored > 0;
    }

    internal class ClipAttackBlock : IBlock, ICloneable
    {
        internal List<AttackDataBlock> AttackData { get; set; } = [];

        public object Clone()
        {
            return new ClipAttackBlock
            {
                AttackData = [.. AttackData]
            };
        }

        public void ReadBlock(TextReader reader)
        {
            int count = int.Parse(reader.ReadLine()!);
            for (int i = 0; i < count; i++)
            {
                AttackDataBlock attackDataBlock = new();
                attackDataBlock.EventName = reader.ReadLine()!;
                attackDataBlock.Mirrored = int.Parse(reader.ReadLine()!);
                int clipCount = int.Parse(reader.ReadLine()!);
                for(int j = 0; j < clipCount; j++)
                    attackDataBlock.Clips.Add(reader.ReadLine()!);

                AttackData.Add(attackDataBlock);
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(AttackData.Count);
            foreach(var attack in AttackData)
            {
                writer.WriteLine(attack.EventName);
                writer.WriteLine(attack.Mirrored);
                writer.WriteLine(attack.Clips.Count);
                foreach (var clip in attack.Clips)
                    writer.WriteLine(clip);
            }
        }


    }
}
