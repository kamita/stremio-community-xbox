using System.Collections.Generic;
using Windows.Data.Json;

namespace StremioCommunity.Models
{
    /// <summary>
    /// A catalog tile (from <c>GET /v1/catalog/{type}/{id}.json</c>) or a meta detail
    /// (from <c>GET /v1/meta/{type}/{id}.json</c>). The same shape is used for both;
    /// detail calls fill in the extra fields (genres, cast, videos, ...).
    /// </summary>
    public sealed class StremioMeta
    {
        /// <summary>Meta type: "movie" or "series".</summary>
        public string Type { get; set; }

        /// <summary>Stremio id (e.g. "tt0000133" for a movie, "tt0944947 S02E10" for an episode).</summary>
        public string Id { get; set; }

        /// <summary>Display name.</summary>
        public string Name { get; set; }

        /// <summary>Poster image URL (may be null).</summary>
        public string Poster { get; set; }

        /// <summary>Background image URL (detail meta).</summary>
        public string Background { get; set; }

        /// <summary>Logo image URL (detail meta).</summary>
        public string Logo { get; set; }

        /// <summary>Plot description (detail meta).</summary>
        public string Description { get; set; }

        /// <summary>Release info: year or season/episode designator.</summary>
        public string ReleaseInfo { get; set; }

        /// <summary>Number of videos (seasons for a series) on the meta, when reported.</summary>
        public long? VideoCount { get; set; }

        /// <summary>Genres (detail meta).</summary>
        public IReadOnlyList<string> Genres { get; set; } = new List<string>();

        /// <summary>Cast list (detail meta).</summary>
        public IReadOnlyList<string> Cast { get; set; } = new List<string>();

        /// <summary>Runtime in seconds (detail meta).</summary>
        public long? Runtime { get; set; }

        /// <summary>IMDb rating 0-10 (detail meta).</summary>
        public double? ImdbRating { get; set; }

        /// <summary>For a series: its videos (episodes) with their own id/name/overview.</summary>
        public IReadOnlyList<StremioVideo> Videos { get; set; } = new List<StremioVideo>();

        /// <summary>Parses a single meta object from a Stremio API response.</summary>
        public static StremioMeta FromJson(JsonObject json, string type, string id)
        {
            var meta = new StremioMeta
            {
                Type = type,
                Id = id,
                Name = json.GetNamedString("name", id),
            };

            if (json.TryGetValue("poster", out var poster) && poster.ValueType == JsonValueType.String)
            {
                meta.Poster = poster.GetString();
            }

            meta.Background = ReadStringOrNull(json, "background");
            meta.Logo = ReadStringOrNull(json, "logo");
            meta.Description = ReadStringOrNull(json, "description");
            meta.ReleaseInfo = ReadStringOrNull(json, "releaseInfo");

            if (json.TryGetValue("videoCount", out var videoCount) && videoCount.ValueType == JsonValueType.Number)
            {
                meta.VideoCount = ReadLongOrNull(videoCount);
            }

            if (json.TryGetValue("imdbRating", out var imdb) && imdb.ValueType == JsonValueType.Number)
            {
                meta.ImdbRating = ReadDoubleOrNull(imdb);
            }

            if (json.TryGetValue("runtime", out var runtime) && runtime.ValueType == JsonValueType.Number)
            {
                meta.Runtime = ReadLongOrNull(runtime);
            }

            meta.Genres = ReadStringList(json, "genres");
            meta.Cast = ReadStringList(json, "cast");

            var list = new List<StremioVideo>();
            if (json.TryGetValue("videos", out var videos) && videos.ValueType == JsonValueType.Array)
            {
                foreach (var value in videos.GetArray())
                {
                    if (value.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var videoJson = value.GetObject();
                    var video = new StremioVideo
                    {
                        Id = videoJson.GetNamedString("id", string.Empty),
                        Name = videoJson.GetNamedString("name", string.Empty),
                        Overview = ReadStringOrNull(videoJson, "overview"),
                    };

                    if (videoJson.TryGetValue("poster", out var videoPoster) && videoPoster.ValueType == JsonValueType.String)
                    {
                        video.Poster = videoPoster.GetString();
                    }

                    if (videoJson.TryGetValue("imdbRating", out var videoImdb) && videoImdb.ValueType == JsonValueType.Number)
                    {
                        video.ImdbRating = ReadDoubleOrNull(videoImdb);
                    }

                    list.Add(video);
                }
            }

            meta.Videos = list;

            return meta;
        }

        private static string ReadStringOrNull(JsonObject json, string name)
        {
            if (json.TryGetValue(name, out var value) && value.ValueType == JsonValueType.String)
            {
                return value.GetString();
            }

            return null;
        }

        private static long? ReadLongOrNull(IJsonValue value)
        {
            return long.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result)
                ? result
                : (long?)null;
        }

        private static double? ReadDoubleOrNull(IJsonValue value)
        {
            return double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result)
                ? result
                : (double?)null;
        }

        private static IReadOnlyList<string> ReadStringList(JsonObject json, string name)
        {
            var list = new List<string>();
            if (json.TryGetValue(name, out var value) && value.ValueType == JsonValueType.Array)
            {
                foreach (var item in value.GetArray())
                {
                    if (item.ValueType == JsonValueType.String)
                    {
                        list.Add(item.GetString());
                    }
                }
            }

            return list;
        }
    }

    /// <summary>A single video (episode) listed on a series meta.</summary>
    public sealed class StremioVideo
    {
        /// <summary>Episode id, e.g. "tt0944947 S01E01".</summary>
        public string Id { get; set; }

        /// <summary>Episode name.</summary>
        public string Name { get; set; }

        /// <summary>Episode poster URL (may be null).</summary>
        public string Poster { get; set; }

        /// <summary>Episode overview (may be null).</summary>
        public string Overview { get; set; }

        /// <summary>IMDb rating for the episode (may be null).</summary>
        public double? ImdbRating { get; set; }
    }
}
