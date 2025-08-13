namespace SECmd.AnimData
{
    internal interface IBlock
    {
        public abstract void WriteBlock(TextWriter writer);
        public abstract void ReadBlock(TextReader reader);
    }
}
