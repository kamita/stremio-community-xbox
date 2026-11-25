using System;
using System.Collections.Generic;
using StremioCommunity.Core;
using Windows.Data.Json;

namespace StremioCommunity.Models
{
    /// <summary>A playable stream from <c>GET /v1/streams/{type}/{id}.json</c> or an addon.</summary>
    public sealed class StremioStream
    {
        /// <summary>Stream name (often "Torrent" / "DEBRID" and the quality tags).</summary>
        public string Name { get; set; }

        /// <summary>Stream description (often holds the file name).</summary>
        public string Description { get; set; }

        /// <summary>Direct HTTP(S) file URL, when the stream provides one.</summary>
        public string Url { get; set; }

        /// <summary>File name from behaviorHints, when the addon set one.</summary>
        public string FileName { get; set; }

        /// <summary>True when the addon asked for custom request headers (not honored by the native player).</summary>
        public bool HasRequestHeaders { get; set; }

        /// <summary>Subtitles bundled with the stream itself (from <c>stream.subtitles</c>).</summary>
        public List<SubtitleSource> Subtitles { get; set; } = new List<SubtitleSource>();

        /// <summary>Builds a stream from a Stremio API stream object.</summary>
        public static StremioStream FromJson(JsonObject json)
        {
            var stream = new StremioStream
            {
                Name = json.GetNamedString("name", string.Empty),
                Description = json.GetNamedString("description", json.GetNamedString("title", string.Empty)),
            };

            if (json.TryGetValue("url", out var url) && url.ValueType == JsonValueType.String)
            {
                stream.Url = url.GetString();
            }

            if (json.TryGetValue("behaviorHints", out var hints) && hints.ValueType == JsonValueType.Object)
            {
                var hintsJson = hints.GetObject();
                stream.FileName = ReadStringOrNull(hintsJson, "filename");
                stream.HasRequestHeaders = hintsJson.ContainsKey("proxyHeaders");
            }

            if (json.TryGetValue("subtitles", out var subtitles) && subtitles.ValueType == JsonValueType.Array)
            {
                foreach (var value in subtitles.GetArray())
                {
                    if (value.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var subJson = value.GetObject();
                    var subUrl = ReadStringOrNull(subJson, "url");
                    if (string.IsNullOrEmpty(subUrl))
                    {
                        continue;
                    }

                    var lang = ReadStringOrNull(subJson, "lang") ?? string.Empty;
                    stream.Subtitles.Add(new SubtitleSource(subUrl, lang, (string.IsNullOrEmpty(lang) ? "?" : lang) + " (stream)"));
                }
            }

            return stream;
        }

        private static string ReadStringOrNull(JsonObject json, string name)
        {
            if (json.TryGetValue(name, out var value) && value.ValueType == JsonValueType.String)
            {
                return value.GetString();
            }

            return null;
        }
    }

    /// <summary>
    /// Everything the native player needs for one title: the chosen stream, its subtitles
    /// (stream-bundled + addon-collected, in shell.js <c>collectSubtitles</c> order) and the
    /// account-level language preferences from the profile.
    /// </summary>
    public sealed class PlaybackSelection
    {
        /// <summary>The chosen stream (url, name, description, file name).</summary>
        public StremioStream Stream { get; set; }

        /// <summary>All subtitles to attach: stream first, then addons, preferred language sorted to the top.</summary>
        public IReadOnlyList<SubtitleSource> Subtitles { get; set; } = new List<SubtitleSource>();

        /// <summary>Account-level preferred subtitle language (ISO 639-1, e.g. "es"); empty = auto.</summary>
        public string PreferredSubLang { get; set; } = string.Empty;

        /// <summary>Account-level preferred audio language (ISO 639-1, e.g. "en"); empty = auto.</summary>
        public string PreferredAudioLang { get; set; } = string.Empty;

        /// <summary>Builds the legacy <see cref="PlayRequest"/> handed to the player from this selection.</summary>
        public PlayRequest ToPlayRequest()
        {
            var subtitles = new JsonArray();
            foreach (var subtitle in Subtitles)
            {
                subtitles.Add(new JsonObject
                {
                    ["url"] = JsonValue.CreateStringValue(subtitle.Url),
                    ["lang"] = JsonValue.CreateStringValue(subtitle.Language),
                    ["label"] = JsonValue.CreateStringValue(subtitle.Label),
                });
            }

            var message = new JsonObject
            {
                ["url"] = JsonValue.CreateStringValue(Stream.Url ?? string.Empty),
                ["name"] = JsonValue.CreateStringValue(Stream.Name ?? string.Empty),
                ["description"] = JsonValue.CreateStringValue(Stream.Description ?? string.Empty),
                ["filename"] = JsonValue.CreateStringValue(Stream.FileName ?? string.Empty),
                ["hasRequestHeaders"] = JsonValue.CreateBooleanValue(Stream.HasRequestHeaders),
                ["preferredSubLang"] = JsonValue.CreateStringValue(PreferredSubLang ?? string.Empty),
                ["preferredAudioLang"] = JsonValue.CreateStringValue(PreferredAudioLang ?? string.Empty),
                ["subtitles"] = subtitles,
            };

            return PlayRequest.FromJson(message);
        }
    }
}
