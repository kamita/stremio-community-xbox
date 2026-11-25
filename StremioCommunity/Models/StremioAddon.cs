using System;
using System.Collections.Generic;
using Windows.Data.Json;

namespace StremioCommunity.Models
{
    /// <summary>
    /// An addon installed on the user's Stremio account (from <c>GET /v1/profile.json</c>),
    /// with its <c>manifest.json</c> merged in.
    /// </summary>
    public sealed class StremioAddon
    {
        /// <summary>Addon id (from the manifest).</summary>
        public string Id { get; set; }

        /// <summary>Addon display name (from the manifest, e.g. "OpenSubtitles").</summary>
        public string Name { get; set; }

        /// <summary>Full transport URL including <c>/manifest.json</c>, as stored in the profile.</summary>
        public string TransportUrl { get; set; }

        /// <summary>Addon types (e.g. "movie", "series").</summary>
        public List<string> Types { get; set; } = new List<string>();

        /// <summary>Addon resource definitions (objects with name/types/idPrefixes).</summary>
        public List<JsonObject> Resources { get; set; } = new List<JsonObject>();

        /// <summary>Creates an addon from a profile entry plus its manifest (which may be null).</summary>
        public static StremioAddon FromProfileEntry(JsonObject entry, JsonObject manifest)
        {
            var addon = new StremioAddon
            {
                TransportUrl = entry.GetNamedString("transportUrl", string.Empty),
            };

            if (manifest == null)
            {
                return addon;
            }

            addon.Id = manifest.GetNamedString("id", string.Empty);
            addon.Name = manifest.GetNamedString("name", addon.Id);

            if (manifest.TryGetValue("types", out var typesValue) && typesValue.ValueType == JsonValueType.Array)
            {
                foreach (var value in typesValue.GetArray())
                {
                    if (value.ValueType == JsonValueType.String)
                    {
                        addon.Types.Add(value.GetString());
                    }
                }
            }

            if (manifest.TryGetValue("resources", out var resourcesValue) && resourcesValue.ValueType == JsonValueType.Array)
            {
                foreach (var value in resourcesValue.GetArray())
                {
                    if (value.ValueType == JsonValueType.Object)
                    {
                        addon.Resources.Add(value.GetObject());
                    }
                }
            }

            return addon;
        }

        /// <summary>
        /// Returns true when this addon's manifest advertises a "subtitles" resource that covers
        /// the given meta type and id (same rules as the old shell.js <c>providesSubtitles</c>).
        /// </summary>
        public bool ProvidesSubtitles(string type, string id)
        {
            foreach (var resource in Resources)
            {
                if (!resource.ContainsKey("name") || resource.GetNamedString("name", string.Empty) != "subtitles")
                {
                    continue;
                }

                var types = new List<string>();
                if (resource.TryGetValue("types", out var typesValue) && typesValue.ValueType == JsonValueType.Array)
                {
                    foreach (var value in typesValue.GetArray())
                    {
                        if (value.ValueType == JsonValueType.String)
                        {
                            types.Add(value.GetString());
                        }
                    }
                }

                if (types.Count > 0 && types.FindIndex(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) < 0)
                {
                    continue;
                }

                var prefixes = new List<string>();
                if (resource.TryGetValue("idPrefixes", out var prefixesValue) && prefixesValue.ValueType == JsonValueType.Array)
                {
                    foreach (var value in prefixesValue.GetArray())
                    {
                        if (value.ValueType == JsonValueType.String)
                        {
                            prefixes.Add(value.GetString());
                        }
                    }
                }

                if (prefixes.Count == 0)
                {
                    return true;
                }

                foreach (var prefix in prefixes)
                {
                    if (id.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
