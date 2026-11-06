using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Per-library home screen visibility for boxset (collection) libraries. The state is kept in
    /// a separate JSON file so the main plugin options never contain a collection property.
    /// </summary>
    public class BoxSetHomeVisibilityStore
    {
        private const string StoreFileName = "StrmAssistant.BoxSetHomeVisibility.json";

        private readonly IJsonSerializer _jsonSerializer;
        private readonly string _storePath;
        private readonly object _sync = new object();
        private readonly HashSet<string> _hiddenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public BoxSetHomeVisibilityStore(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
            _storePath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, StoreFileName);
            Load();
        }

        public List<string> GetHiddenIds()
        {
            lock (_sync)
            {
                return _hiddenIds.ToList();
            }
        }

        public bool IsHidden(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            lock (_sync)
            {
                return _hiddenIds.Contains(id);
            }
        }

        public void SetHidden(string id, bool hidden)
        {
            if (string.IsNullOrEmpty(id)) return;

            lock (_sync)
            {
                var changed = hidden ? _hiddenIds.Add(id) : _hiddenIds.Remove(id);

                if (!changed) return;

                Persist();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_storePath)) return;

                var store = _jsonSerializer.DeserializeFromFile<BoxSetHomeVisibilityFile>(_storePath);

                if (store?.HiddenIds is null) return;

                foreach (var id in store.HiddenIds.Where(i => !string.IsNullOrWhiteSpace(i)))
                {
                    _hiddenIds.Add(id.Trim());
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger?.Debug("BoxSetHomeVisibility - Load failed: " + e.Message);
            }
        }

        private void Persist()
        {
            try
            {
                _jsonSerializer.SerializeToFile(new BoxSetHomeVisibilityFile
                {
                    HiddenIds = _hiddenIds.ToList()
                }, _storePath);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger?.Debug("BoxSetHomeVisibility - Persist failed: " + e.Message);
            }
        }

        private class BoxSetHomeVisibilityFile
        {
            public List<string> HiddenIds { get; set; } = new List<string>();
        }
    }
}
