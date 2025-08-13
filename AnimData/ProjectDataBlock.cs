namespace SECmd.AnimData
{
    internal class ProjectDataBlock : IBlock,ILineCounter
    {
        List<ClipMovementData> movementData = [];

        public List<ClipMovementData> GetMovementData() { return movementData; }
        public int LineCount 
        {
            get { return movementData.Aggregate(0, (acc, x) => acc += x.LineCount); }
        }
        public void ReadBlock(TextReader reader)
        {
            while(reader.Peek() >= 0)
            {
                ClipMovementData clipMovementData = new();
                clipMovementData.ReadBlock(reader);
                movementData.Add(clipMovementData);
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            foreach(var clipMovementData in movementData)
            {
                clipMovementData.WriteBlock(writer);
            }
        }
    }
}
