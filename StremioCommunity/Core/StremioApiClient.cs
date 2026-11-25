using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StremioCommunity.Core;
using StremioCommunity.Models;
using Windows.Data.Json;
using Windows.Web.Http;

namespace StremioCommunity.Core
{
    /// <summary>
    /// Client for the Stremio REST API (https://api.stremio.com) and for addon transports.
    /// Uses Windows.Web.Http (System.Net.Http is unreliable on the Xbox UWP build — see the
    /// original MainPage notes) and Windows.Data.Json for parsing. The HttpClient is
    /// apartment-threaded, so create it lazily and always call this client from the UI thread.
    /// </summary>
    public sealed class StremioApiClient
    {
        private const string ApiBase = "https://api.stremio.com/v1";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private static readonly object ClientLock = new object();
        private static HttpClient _client;

        /// <summary>An entry in a catalog row (the Home page's categories).</summary>
        public sealed class CatalogEntry
        {
            /// <summary>Category id within the type (e.g. "top", "trending", "new").</summary>
            public string Id { get; set; }

            /// <summary>Display name shown on the category tab (e.g. "Top 100", "Trending").</summary>
            public string Name { get; set; }
        }

        /// <summary>One page of a catalog: the entries found plus a cursor for the next page.</summary>
        public sealed class CatalogPage
        {
            /// <summary>Meta entries on this page (may be empty).</summary>
            public IReadOnlyList<StremioMeta> Metas { get; set; } = new List<StremioMeta>();

            /// <summary>"skip" offset to pass to the next call to get more (null when done).</summary>
            public int? NextSkip { get; set; }
        }

        /// <summary>A stream row: the stream plus optional addon metadata.</summary>
        public sealed class StreamRow
        {
            /// <summary>The parsed stream.</summary>
            public StremioStream Stream { get; set; }

            /// <summary>Source: "stremio" or an addon name.</summary>
            public string Name { get; set; } = "stremio";
        }

        /// <summary>Subtitle list result: sources sorted (preferred language first) and capped.</summary>
        public sealed class SubtitleResult
        {
            /// <summary>The collected subtitle sources (stream first, then addons).</summary>
            public IReadOnlyList<SubtitleSource> Subtitles { get; set; } = new List<SubtitleSource>();
        }

        private static HttpClient Client
        {
            get
            {
                lock (ClientLock)
                {
                    if (_client == null)
                    {
                        // Windows.Web.Http.HttpClient has no Timeout property on this platform;
                        // the per-request timeout is enforced in GetJsonObjectAsync via Task.WhenAny.
                        _client = new HttpClient();
                    }

                    return _client;
                }
            }
        }

        // ---- Core API ----------------------------------------------------------------------

        /// <summary>Gets the user profile (addons + language settings). Null when not signed in.</summary>
        public async Task<StremioProfile> GetProfileAsync()
        {
            var json = await GetJsonObjectAsync(ApiBase + "/profile.json");
            return json == null ? null : StremioProfile.FromJson(json);
        }

        /// <summary>Lists the available categories for a type (from <c>/v1/catalog/{type}.json</c>).</summary>
        public async Task<IReadOnlyList<CatalogEntry>> GetCatalogEntriesAsync(string type)
        {
            var entries = new List<CatalogEntry>();
            var json = await GetJsonObjectAsync(ApiBase + "/catalog/" + type + ".json");
            if (json == null || !json.TryGetValue("catalog", out var catalog) || catalog.ValueType != JsonValueType.Array)
            {
                return entries;
            }

            foreach (var value in catalog.GetArray())
            {
                if (value.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var entryJson = value.GetObject();
                var id = entryJson.GetNamedString("id", string.Empty);
                if (id.Length > 0)
                {
                    entries.Add(new CatalogEntry { Id = id, Name = entryJson.GetNamedString("name", id) });
                }
            }

            return entries;
        }

        /// <summary>
        /// Gets a page of metas from <c>/v1/catalog/{type}/{id}.json?skip={skip}&amp;limit={limit}</c>.
        /// </summary>
        public async Task<CatalogPage> GetCatalogPageAsync(string type, string catalogId, int skip, int limit)
        {
            var page = new CatalogPage();
            var url = string.Format("{0}/catalog/{1}/{2}.json?skip={3}&limit={4}",
                ApiBase, type, catalogId, skip, limit);
            var json = await GetJsonObjectAsync(url);
            if (json == null || !json.TryGetValue("metas", out var metas) || metas.ValueType != JsonValueType.Array)
            {
                return page;
            }

            var list = new List<StremioMeta>();
            foreach (var value in metas.GetArray())
            {
                if (value.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var metaJson = value.GetObject();
                var metaId = metaJson.GetNamedString("id", string.Empty);
                if (metaId.Length == 0)
                {
                    continue;
                }

                list.Add(StremioMeta.FromJson(metaJson, type, metaId));
            }

            page.Metas = list;
            page.NextSkip = list.Count == limit ? skip + limit : (int?)null;
            return page;
        }

        /// <summary>Gets full meta detail for a title or an episode (videos included for series).</summary>
        public async Task<StremioMeta> GetMetaAsync(string type, string id)
        {
            var parts = id.Split(' ');
            if (parts.Length == 0)
            {
                return null;
            }

            // The meta endpoint always keys on the base (first) id.
            var metaId = parts[0];
            var json = await GetJsonObjectAsync(ApiBase + "/meta/" + type + "/" + metaId + ".json");
            return json == null ? null : StremioMeta.FromJson(json, type, id);
        }

        /// <summary>
        /// Gets all stream rows for a title/episode: Stremio's core streams first, then each
        /// addon's streams (from the profile's addons) — same union as the old shell.js.
        /// </summary>
        public async Task<IReadOnlyList<StreamRow>> GetStreamsAsync(StremioProfile profile, string type, string id)
        {
            var rows = new List<StreamRow>();

            foreach (var stream in await GetRawStreamsAsync(ApiBase + "/streams/" + type + "/" + id + ".json"))
            {
                rows.Add(new StreamRow { Stream = stream, Name = "stremio" });
            }

            if (profile != null)
            {
                foreach (var addon in profile.Addons)
                {
                    if (!AddonCovers(addon, type))
                    {
                        continue;
                    }

                    var baseUri = StripManifestPath(addon.TransportUrl);
                    var streams = await GetRawStreamsAsync(baseUri + "/streams/" + type + "/" + id + ".json");
                    foreach (var stream in streams)
                    {
                        rows.Add(new StreamRow { Stream = stream, Name = addon.Name ?? addon.Id ?? "addon" });
                    }
                }
            }

            return rows;
        }

        // ---- Subtitle collection (ported from shell.js collectSubtitles) -------------------

        private const int SubtitleTimeoutSeconds = 4;
        private const int MaxSubtitles = 20; // the native player downloads each one when the file opens
        private const int MaxPerLanguage = 3;

        /// <summary>
        /// Collects subtitles for a stream: the stream's own subtitles first, then each addon
        /// that provides subtitles for the type/id, preferred language sorted to the top,
        /// capped at <see cref="MaxSubtitles"/> total and <see cref="MaxPerLanguage"/> per language
        /// (identical rules to the old shell.js <c>collectSubtitles</c>).
        /// </summary>
        public async Task<SubtitleResult> CollectSubtitlesAsync(StremioProfile profile, StremioStream stream, string type, string id)
        {
            var list = new List<SubtitleSource>(stream.Subtitles);

            if (profile != null)
            {
                foreach (var addon in profile.Addons)
                {
                    if (!addon.ProvidesSubtitles(type, id))
                    {
                        continue;
                    }

                    var baseUri = StripManifestPath(addon.TransportUrl);
                    var url = baseUri + "/subtitles/" + Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(id) + ".json";
                    var json = await GetJsonObjectAsync(url, TimeSpan.FromSeconds(SubtitleTimeoutSeconds));
                    if (json == null || !json.TryGetValue("subtitles", out var subtitles) || subtitles.ValueType != JsonValueType.Array)
                    {
                        continue;
                    }

                    var name = addon.Name ?? "addon";
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
                        list.Add(new SubtitleSource(subUrl, lang, (string.IsNullOrEmpty(lang) ? "?" : lang) + " (" + name + ")"));
                    }
                }
            }

            var preferred = profile?.SubtitlesLanguage ?? string.Empty;
            if (!string.IsNullOrEmpty(preferred))
            {
                // Stable partition: preferred language first (matching shell.js's comparator).
                list.Sort((a, b) =>
                    (b.Language == preferred ? 1 : 0) - (a.Language == preferred ? 1 : 0));
            }

            var perLanguage = new Dictionary<string, int>();
            var picked = new List<SubtitleSource>();
            foreach (var subtitle in list)
            {
                var lang = string.IsNullOrEmpty(subtitle.Language) ? "" : subtitle.Language;
                int count;
                perLanguage.TryGetValue(lang, out count);
                if (count >= MaxPerLanguage)
                {
                    continue;
                }

                perLanguage[lang] = count + 1;
                picked.Add(subtitle);
                if (picked.Count >= MaxSubtitles)
                {
                    break;
                }
            }

            return new SubtitleResult { Subtitles = picked };
        }

        // ---- Addon helpers -------------------------------------------------------------------

        /// <summary>True when the addon serves the given meta type (empty types = all types).</summary>
        private static bool AddonCovers(StremioAddon addon, string type)
        {
            if (addon.Types.Count == 0)
            {
                return true;
            }

            foreach (var addonType in addon.Types)
            {
                if (string.Equals(addonType, type, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Turns "https://host/path/manifest.json" into "https://host/path".</summary>
        public static string StripManifestPath(string transportUrl)
        {
            if (transportUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                return transportUrl.Substring(0, transportUrl.Length - "/manifest.json".Length);
            }

            return transportUrl;
        }

        // ---- HTTP / JSON plumbing --------------------------------------------------------------

        private async Task<IReadOnlyList<StremioStream>> GetRawStreamsAsync(string url)
        {
            var json = await GetJsonObjectAsync(url);
            if (json == null || !json.TryGetValue("streams", out var streams) || streams.ValueType != JsonValueType.Array)
            {
                return new List<StremioStream>();
            }

            var list = new List<StremioStream>();
            foreach (var value in streams.GetArray())
            {
                if (value.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                list.Add(StremioStream.FromJson(value.GetObject()));
            }

            return list;
        }

        private async Task<JsonObject> GetJsonObjectAsync(string url, TimeSpan timeout = default)
        {
            var effective = timeout == default ? RequestTimeout : timeout;
            try
            {
                var client = Client;
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                var send = client.SendRequestAsync(request).AsTask();
                if (await Task.WhenAny(send, Task.Delay(effective)) != send)
                {
                    // The request is abandoned; SendRequestAsync does not support CancellationTokens
                    // on this platform, so we simply stop waiting and drop the response when it arrives.
                    FileLog.Write("warn", "GET timed out: " + url);
                    return null;
                }

                using (var response = await send)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        FileLog.Write("warn", "HTTP " + (int)response.StatusCode + ": " + url);
                        return null;
                    }

                    var text = await ReadHttpContentAsString(response.Content);
                    return JsonObject.Parse(text);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("warn", "GET failed " + url + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Reads an <see cref="IHttpContent"/> as a UTF-8 string via <c>ReadAsInputStreamAsync</c>,
        /// copying each <c>IBuffer</c> block through a <c>DataReader</c> (avoids IBuffer.CopyTo,
        /// which is not available on this platform).
        /// </summary>
        private static async Task<string> ReadHttpContentAsString(IHttpContent content)
        {
            using (var stream = await content.ReadAsInputStreamAsync())
            {
                var parts = new List<byte>();
                var blockBuffer = new Windows.Storage.Streams.Buffer(64 * 1024);
                const int maxBytes = 20 * 1024 * 1024; // 20 MB safety cap
                while (parts.Count < maxBytes)
                {
                    var read = await stream.ReadAsync(blockBuffer, 64 * 1024, Windows.Storage.Streams.InputStreamOptions.None);
                    if (read.Length == 0)
                    {
                        break;
                    }

                    var reader = Windows.Storage.Streams.DataReader.FromBuffer(read);
                    var block = new byte[(int)read.Length];
                    reader.ReadBytes(block);
                    parts.AddRange(block);
                }

                return System.Text.Encoding.UTF8.GetString(parts.ToArray());
            }
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
}
