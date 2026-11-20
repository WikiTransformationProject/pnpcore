using System;
using System.Collections.Generic;
// v============= HEU/LLM: Check nested column selections. ==========
using System.Linq;
// ^===================================================================

namespace PnP.Core.Model
{
    /// <summary>
    /// Holds information that describes how a fields is expanded. Is populated when .QueryProperties() is used 
    /// </summary>
    internal sealed class EntityFieldExpandInfo
    {
        public string Name { get; set; }
        public Type Type { get; set; }
        public bool Expandable { get; set; }
        // v============= HEU/LLM: Select an expando column by its name. ==========
        public bool IsExpandoField { get; set; }
        // written by LLM, 2026-11-14
        public bool HasExpandoFields => IsExpandoField || Fields.Any(field => field.HasExpandoFields);
        // ^===================================================================
        public List<EntityFieldExpandInfo> Fields { get; } = new List<EntityFieldExpandInfo>();

    }
}
