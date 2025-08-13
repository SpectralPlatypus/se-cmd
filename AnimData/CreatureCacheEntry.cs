using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SECmd.AnimData
{
    internal class CreatureCacheEntry : CacheEntry
    {
        internal ProjectAttackListBlock AttackList { get; private set; } = new();

        public CreatureCacheEntry() { }
        public CreatureCacheEntry(string name, ProjectBlock block, ProjectDataBlock movements, ProjectAttackListBlock attackList) : base(name, block, movements)
        {
            this.AttackList = attackList;
        }


    }
}
