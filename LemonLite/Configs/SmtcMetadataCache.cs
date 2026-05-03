using System.Collections.Generic;

namespace LemonLite.Configs;

public class SmtcMetadataCacheEntry
{
    public string Title { get; init; } = string.Empty;
    public string[] Artists { get; init; } = [];
    public string Album { get; init; } = string.Empty;
    public int DurationMs { get; init; }

    /// <summary>
    /// 每个sourceId对应一个歌词文件ID，sourceId来自<see cref="LemonLite.Sources.ILyricSource"/>
    /// </summary>
    public Dictionary<string, string> LyricFileIds { get; init; } = [];
}

public class SmtcMetadataCache
{
    public Dictionary<string, SmtcMetadataCacheEntry> Cache { get; } = [];
}
