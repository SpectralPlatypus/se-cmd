using System.Numerics;

namespace SECmd.AnimData
{
    internal class ClipMovementData : IBlock, ILineCounter, ICloneable
    {
        public int CacheIndex { get; private set; } = 0;
        float duration = 0f;
        List<(float, Vector3)> translations = [];
        List<(float, Quaternion)> rotations = [];

        public int LineCount { get { return 4 + translations.Count + rotations.Count + 1; } }

        public void ReadBlock(TextReader reader)
        {
            CacheIndex = int.Parse(reader.ReadLine()!);
            duration = float.Parse(reader.ReadLine()!);
            int count = int.Parse((reader.ReadLine()!));
            for (int i = 0; i < count; ++i)
            {
                var entries = reader.ReadLine()?.Split(' ');
                if (entries?.Length != 4)
                    throw new IOException("Invalid translation format");
                translations.Add(
                    (float.Parse(entries[0]),
                    new(float.Parse(entries[1]), float.Parse(entries[2]), float.Parse(entries[3])
                    )));
            }
            count = int.Parse((reader.ReadLine()!));
            for (int i = 0; i < count; ++i)
            {
                var entries = reader.ReadLine()?.Split(' ');
                if (entries?.Length != 5)
                    throw new IOException("Invalid translation format");
                rotations.Add(
                    (float.Parse(entries[0]),
                    new(float.Parse(entries[1]), float.Parse(entries[2]), float.Parse(entries[3]), float.Parse(entries[4])
                    )));
            }
            reader.ReadLine();
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(CacheIndex);
            writer.WriteLine(duration);
            writer.WriteLine(translations.Count);
            foreach (var tr in translations)
            {
                // Lowercase exponent notation
                writer.WriteLine(string.Join(' ', tr.Item1, tr.Item2.X, tr.Item2.Y, tr.Item2.Z).ToLower());
            }
            writer.WriteLine(rotations.Count);
            foreach (var tr in rotations)
            {
                writer.WriteLine(string.Join(' ', tr.Item1, tr.Item2.X, tr.Item2.Y, tr.Item2.Z, tr.Item2.W).ToLower());
            }
            writer.WriteLine();
        }

        public object Clone()
        {
            var clone = new ClipMovementData
            {
                CacheIndex = CacheIndex,
                duration = duration,
                translations = [.. translations],
                rotations = [.. rotations]
            };

            return clone;
        }
    }
}
