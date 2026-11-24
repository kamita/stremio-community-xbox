using System;
using System.Text.RegularExpressions;

namespace StremioCommunity.Core
{
    /// <summary>
    /// Modifies the timestamps in SRT or WebVTT subtitle content to apply a delay (seconds)
    /// and a speed multiplier. This works on the Xbox where the SubDelay/SubRate APIs are absent.
    /// </summary>
    public static class SubtitleTimingModifier
    {
        /// <summary>
        /// Optional diagnostic hook. The host can attach a logger so the modifier can
        /// report when it fails to find any timestamps in the content (todo round 7 #2).
        /// </summary>
        public static Action<string> OnDiagnostic;

        /// <summary>
        /// Returns the subtitle content with delay (seconds) and speed (multiplier) applied to all cue timings.
        /// If both are at their defaults (0 delay, 1.0 speed) the content is returned unchanged.
        /// </summary>
        public static string Modify(string content, double delaySeconds, double speed)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            if (Math.Abs(delaySeconds) < 0.001 && Math.Abs(speed - 1.0) < 0.001)
            {
                return content;
            }

            var trimmed = content.TrimStart();
            var isVtt = trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase);

            // Both SRT and VTT use "start --> end" cues. Hours are mandatory in SRT,
            // optional in VTT. The millisecond separator is a comma in SRT, a period in
            // VTT, but addons frequently mix the two — accept both. Milliseconds are
            // optional in some hand-authored files; accept them optionally.
            var time = @"\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?";
            var pattern = @"(" + time + @")\s*-->\s*(" + time + @")";

            var rx = new Regex(pattern);
            var matches = rx.Matches(content);
            if (matches.Count == 0)
            {
                // Not SRT/VTT — likely ASS/SSA, JSON, or binary. Report the head so the
                // next session.log reveals the actual format (todo round 7 #2).
                var head = content.Length > 120 ? content.Substring(0, 120) : content;
                OnDiagnostic?.Invoke("SubtitleTimingModifier: no timestamps matched (len=" +
                    content.Length + ", head='" + head.Replace("\n", "\\n") + "')");
                return content;
            }
            return rx.Replace(content, match =>
            {
                var startStr = match.Groups[1].Value;
                var endStr = match.Groups[2].Value;
                var newStart = FormatTime(ModifyTime(ParseTime(startStr), delaySeconds, speed), isVtt);
                var newEnd = FormatTime(ModifyTime(ParseTime(endStr), delaySeconds, speed), isVtt);
                return newStart + " --> " + newEnd;
            });
        }

        private static double ModifyTime(double seconds, double delay, double speed)
        {
            return Math.Max(0, seconds / speed + delay);
        }

        private static double ParseTime(string time)
        {
            // Handles: HH:MM:SS,mmm  |  HH:MM:SS.mmm  |  MM:SS.mmm
            // Each position is base-60 (hours, minutes, seconds) + milliseconds.
            // Example: "00:01:30,500" = 90.5 seconds.
            var sep = time.IndexOfAny(new[] { ',', '.' });
            var ms = 0;
            var timePart = time;
            if (sep >= 0)
            {
                ms = int.Parse(time.Substring(sep + 1));
                timePart = time.Substring(0, sep);
            }

            var parts = timePart.Split(':');
            double total = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                total = total * 60.0 + int.Parse(parts[i]);
            }
            return total + ms / 1000.0;
        }

        private static string FormatTime(double totalSeconds, bool vtt)
        {
            totalSeconds = Math.Max(0, totalSeconds);
            var hours = (int)(totalSeconds / 3600);
            var minutes = (int)((totalSeconds % 3600) / 60);
            var seconds = (int)(totalSeconds % 60);
            var millis = (int)((totalSeconds - Math.Floor(totalSeconds)) * 1000);
            // Guard against rounding that pushes millis to 1000
            if (millis >= 1000)
            {
                millis = 0;
                seconds++;
            }

            if (vtt)
            {
                return hours > 0
                    ? string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}", hours, minutes, seconds, millis)
                    : string.Format("{0:D2}:{1:D2}.{2:D3}", minutes, seconds, millis);
            }
            else
            {
                return string.Format("{0:D2}:{1:D2}:{2:D2},{3:D3}", hours, minutes, seconds, millis);
            }
        }
    }
}
