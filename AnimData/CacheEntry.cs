using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SECmd.AnimData
{
    internal class CacheEntry
    {
        public string Name { get; } = "";
        public ProjectBlock Block { get; } = new();
        public ProjectDataBlock? Movement { get; } = null;

        public CacheEntry() { }

        public CacheEntry(string name, ProjectBlock block, ProjectDataBlock? movements)
        {
            this.Name = name;
            this.Block = block;
            this.Movement = movements;
        }

        public bool HasAnimationCache() => Block.HasAnimationCache;
    }
}
