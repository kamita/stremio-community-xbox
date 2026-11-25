using System.Collections.Generic;
using Windows.Data.Json;

namespace StremioCommunity.Models
{
    /// <summary>The user's Stremio account profile (from <c>GET /v1/profile.json</c>).</summary>
    public sealed class StremioProfile
    {
        /// <summary>Account e-mail / id, when signed in (empty for a guest).</summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>Installed addons with their manifests.</summary>
        public List<StremioAddon> Addons { get; set; } = new List<StremioAddon>();

        /// <summary>Preferred subtitle language (settings.subtitlesLanguage, ISO 639-1); empty = auto.</summary>
        public string SubtitlesLanguage { get; set; } = string.Empty;

        /// <summary>Preferred audio language (settings.defaultAudioLanguage, ISO 639-1); empty = auto.</summary>
        public string DefaultAudioLanguage { get; set; } = string.Empty;

        /// <summary>
        /// Parses a profile JSON object; the addon <c>manifest</c> objects are read inline
        /// (the profile API embeds them in each addon entry).
        /// </summary>
        public static StremioProfile FromJson(JsonObject json)
        {
            var profile = new StremioProfile();

            if (json.TryGetValue("account", out var account) && account.ValueType == JsonValueType.String)
            {
                profile.Account = account.GetString();
            }

            if (json.TryGetValue("settings", out var settings) && settings.ValueType == JsonValueType.Object)
            {
                var settingsJson = settings.GetObject();
                if (settingsJson.TryGetValue("subtitlesLanguage", out var subLang) && subLang.ValueType == JsonValueType.String)
                {
                    profile.SubtitlesLanguage = subLang.GetString();
                }

                if (settingsJson.TryGetValue("defaultAudioLanguage", out var audioLang) && audioLang.ValueType == JsonValueType.String)
                {
                    profile.DefaultAudioLanguage = audioLang.GetString();
                }
            }

            if (json.TryGetValue("addons", out var addons) && addons.ValueType == JsonValueType.Array)
            {
                foreach (var value in addons.GetArray())
                {
                    if (value.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var entry = value.GetObject();
                    var manifest = entry.ContainsKey("manifest") && entry["manifest"].ValueType == JsonValueType.Object
                        ? entry["manifest"].GetObject()
                        : null;
                    var addon = StremioAddon.FromProfileEntry(entry, manifest);
                    if (!string.IsNullOrEmpty(addon.TransportUrl))
                    {
                        profile.Addons.Add(addon);
                    }
                }
            }

            return profile;
        }
    }
}
