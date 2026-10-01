using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PlayPollo.Server;

namespace PlayPollo.Mapping
{
    /// <summary>Remembers which Playnite games have been pushed to the server, and what was pushed.</summary>
    public class ExportStore
    {
        private readonly object gate = new object();
        private readonly string filePath;
        private Dictionary<Guid, ExportEntry> entries = new Dictionary<Guid, ExportEntry>();

        public ExportStore(string filePath)
        {
            this.filePath = filePath;
            Load();
        }

        public ExportEntry Get(Guid gameId)
        {
            lock (gate)
            {
                return entries.TryGetValue(gameId, out var entry) ? entry : null;
            }
        }

        public bool Contains(Guid gameId)
        {
            lock (gate)
            {
                return entries.ContainsKey(gameId);
            }
        }

        public IReadOnlyList<Guid> GameIds
        {
            get
            {
                lock (gate)
                {
                    return entries.Keys.ToList();
                }
            }
        }

        public void Set(Guid gameId, ExportEntry entry)
        {
            lock (gate)
            {
                entries[gameId] = entry;
                Save();
            }
        }

        public void Remove(Guid gameId)
        {
            lock (gate)
            {
                if (entries.Remove(gameId))
                {
                    Save();
                }
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<Guid, ExportEntry>>(File.ReadAllText(filePath));
                    if (loaded != null)
                    {
                        entries = loaded;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                // A corrupt store only costs us the memory of what was exported; re-exporting repairs it.
                entries = new Dictionary<Guid, ExportEntry>();
            }
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(entries, Formatting.Indented));
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            File.Move(temp, filePath);
        }
    }
}
