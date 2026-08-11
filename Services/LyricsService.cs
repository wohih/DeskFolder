using System.Text.Json;
using System.Text;
using System.Net.Http;

namespace DeskFolder.Services;

/// <summary>单行歌词（带时间戳）</summary>
public class LyricLine
{
    public double Time { get; set; }
    public string Text { get; set; } = "";
    public string Translation { get; set; } = "";
    public bool HasTranslation => !string.IsNullOrEmpty(Translation);
}

/// <summary>
/// 歌词搜索服务：通过公共API搜索歌词。
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// 搜索歌词（异步），返回带时间戳的歌词行列表
    /// </summary>
    public static async Task<List<LyricLine>> SearchLyricsAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return new();

        try
        {
            return await SearchViaNetEaseAsync(title, artist);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsService] SearchLyricsAsync error: {ex.Message}");
            return new();
        }
    }

    /// <summary>
    /// 通过网易云音乐API搜索歌词（使用 POST 请求）
    /// </summary>
    private static async Task<List<LyricLine>> SearchViaNetEaseAsync(string title, string artist)
    {
        try
        {
            // 构建搜索关键词
            string keyword = string.IsNullOrEmpty(artist) ? title : $"{title} {artist}";

            // 使用 POST 请求搜索歌曲（GET 请求经常被拒绝）
            var searchUrl = "https://music.163.com/api/search/get";
            var formData = new List<KeyValuePair<string, string>>
            {
                new("s", keyword),
                new("type", "1"),
                new("limit", "5"),
                new("offset", "0")
            };
            var formContent = new FormUrlEncodedContent(formData);

            using var request = new HttpRequestMessage(HttpMethod.Post, searchUrl);
            request.Content = formContent;
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Referer", "https://music.163.com/");
            request.Headers.Add("Cookie", "NMTID=xxx");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            // 调试日志
            System.Diagnostics.Debug.WriteLine($"[LyricsService] Search response length: {json.Length}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var searchResult)) return new();
            if (!searchResult.TryGetProperty("songs", out var songs)) return new();
            if (songs.GetArrayLength() == 0) return new();

            // 在搜索结果中找到最匹配的歌曲
            long songId = 0;
            string matchedTitle = "";
            for (int i = 0; i < songs.GetArrayLength(); i++)
            {
                var song = songs[i];
                string songName = song.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                string songArtist = "";
                if (song.TryGetProperty("artists", out var artistsProp) && artistsProp.GetArrayLength() > 0)
                {
                    songArtist = artistsProp[0].TryGetProperty("name", out var artistProp) ? artistProp.GetString() ?? "" : "";
                }

                // 精确匹配歌曲名
                if (string.Equals(songName, title, StringComparison.OrdinalIgnoreCase))
                {
                    songId = song.GetProperty("id").GetInt64();
                    matchedTitle = songName;
                    break;
                }

                // 模糊匹配：歌曲名包含搜索的标题
                if (songName.Contains(title, StringComparison.OrdinalIgnoreCase) ||
                    title.Contains(songName, StringComparison.OrdinalIgnoreCase))
                {
                    songId = song.GetProperty("id").GetInt64();
                    matchedTitle = songName;
                    break;
                }

                // 第一个结果作为后备
                if (songId == 0)
                {
                    songId = song.GetProperty("id").GetInt64();
                    matchedTitle = songName;
                }
            }

            if (songId == 0) return new();

            System.Diagnostics.Debug.WriteLine($"[LyricsService] Matched: '{matchedTitle}' (id={songId})");

            // 获取歌词
            var lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
            using var lyricRequest = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
            lyricRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            lyricRequest.Headers.Add("Referer", "https://music.163.com/");

            var lyricResponse = await _httpClient.SendAsync(lyricRequest);
            lyricResponse.EnsureSuccessStatusCode();

            string lyricJson = await lyricResponse.Content.ReadAsStringAsync();
            using var lyricDoc = JsonDocument.Parse(lyricJson);
            var lyricRoot = lyricDoc.RootElement;

            string? lrcContent = null;
            string? tlyricContent = null;

            if (lyricRoot.TryGetProperty("lrc", out var lrcElement))
            {
                lrcContent = lrcElement.TryGetProperty("lyric", out var lyricProp) ? lyricProp.GetString() : null;
            }

            if (lyricRoot.TryGetProperty("tlyric", out var tlyricElement))
            {
                tlyricContent = tlyricElement.TryGetProperty("lyric", out var tlyricProp) ? tlyricProp.GetString() : null;
            }

            // 合并原文和翻译为 LyricLine 列表
            if (!string.IsNullOrEmpty(lrcContent))
            {
                var originalLines = ParseLrcToTimedLines(lrcContent);
                var translatedLines = !string.IsNullOrEmpty(tlyricContent)
                    ? ParseLrcToTimedLines(tlyricContent)
                    : new Dictionary<double, string>();

                var lyricLines = new List<LyricLine>();
                foreach (var kvp in originalLines)
                {
                    var line = new LyricLine { Time = kvp.Key, Text = kvp.Value };
                    if (translatedLines.TryGetValue(kvp.Key, out var translation) && !string.IsNullOrEmpty(translation))
                    {
                        line.Translation = translation;
                    }
                    lyricLines.Add(line);
                }

                System.Diagnostics.Debug.WriteLine($"[LyricsService] Got lyrics: {lyricLines.Count} lines");
                return lyricLines;
            }

            return new List<LyricLine>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsService] SearchViaNetEaseAsync error: {ex.Message}");
            return new List<LyricLine>();
        }
    }

    /// <summary>
    /// 解析LRC格式歌词为 时间戳→歌词文本 的字典（处理多时间标签）
    /// </summary>
    private static Dictionary<double, string> ParseLrcToTimedLines(string lrcText)
    {
        var result = new Dictionary<double, string>();
        var lines = lrcText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // LRC行可能包含多个时间标签：[00:01.23][00:15.45]歌词内容
            // 也可能没有时间标签
            string content = line;
            var timeMatches = new List<double>();

            // 提取所有时间标签
            int idx = 0;
            while (idx < content.Length && content[idx] == '[')
            {
                int closeIdx = content.IndexOf(']', idx);
                if (closeIdx < 0) break;

                string tag = content.Substring(idx + 1, closeIdx - idx - 1);

                // 尝试解析为时间 [mm:ss.xx] 或 [mm:ss]
                if (TryParseLrcTime(tag, out double time))
                {
                    timeMatches.Add(time);
                }
                // 忽略非时间标签如 [ti:][ar:][al:][by:]

                idx = closeIdx + 1;
            }

            // 提取歌词内容（去掉所有标签后的部分）
            if (idx > 0)
            {
                content = content.Substring(idx).Trim();
            }
            else
            {
                content = content.Trim();
            }

            if (string.IsNullOrEmpty(content)) continue;

            // 为每个时间标签添加该歌词行
            foreach (var time in timeMatches)
            {
                result[time] = content;
            }

            // 如果没有时间标签，使用行号作为排序键
            if (timeMatches.Count == 0 && !string.IsNullOrEmpty(content))
            {
                result[result.Count * 0.01] = content;
            }
        }

        // 按时间排序
        return result.OrderBy(kvp => kvp.Key)
                     .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>解析 LRC 时间标签 [mm:ss.xx] 或 [mm:ss]</summary>
    private static bool TryParseLrcTime(string tag, out double time)
    {
        time = 0;
        if (string.IsNullOrEmpty(tag)) return false;

        // 格式: mm:ss.xx 或 mm:ss
        var parts = tag.Split(':');
        if (parts.Length < 2) return false;

        if (!double.TryParse(parts[0], out double minutes)) return false;

        if (!double.TryParse(parts[1], out double seconds)) return false;

        time = minutes * 60 + seconds;
        return true;
    }
}
