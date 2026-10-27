namespace NRadioManager.Core;

/// <summary>概览页磁贴定义的元数据（哪些磁贴存在、默认尺寸、可调范围）。</summary>
public sealed record TileDefinition(
    string Id,
    string Title,
    int DefaultColumnSpan,
    int DefaultRowSpan,
    int MinColumnSpan,
    int MaxColumnSpan,
    int MinRowSpan,
    int MaxRowSpan);

/// <summary>
/// 磁贴布局管理：负责默认布局、用户布局、新增磁贴的兼容与校验。
/// 全局布局（位置/尺寸）与磁贴外观（标题开关）统一保存在设置文件里，互不覆盖。
/// </summary>
public static class TileLayout
{
    public const int Columns = 4;

    public static readonly IReadOnlyList<TileDefinition> Definitions = new List<TileDefinition>
    {
        new("traffic-overview", "月套餐与总流量", 1, 1, 1, 1, 1, 1),
        new("traffic-trend", "流量统计", 1, 1, 1, 1, 1, 1),
        new("speed-chart", "实时速率", 1, 1, 1, 1, 1, 1),
        new("signal", "信号质量", 1, 1, 1, 1, 1, 1),
        new("resource-health", "设备资源", 1, 1, 1, 1, 1, 1),
        new("status-summary", "连接状态", 1, 1, 1, 1, 1, 1),
        new("device-info", "设备信息", 1, 1, 1, 1, 1, 1),
        new("network-detail", "移动网络", 1, 1, 1, 1, 1, 1),
    };

    public static TileDefinition? Find(string id) => Definitions.FirstOrDefault(d => d.Id == id);

    /// <summary>生成默认布局。</summary>
    public static List<TileState> CreateDefault()
    {
        var list = new List<TileState>();
        var order = 0;
        foreach (var d in Definitions)
            list.Add(new TileState
            {
                Id = d.Id,
                Order = order++,
                ColumnSpan = d.DefaultColumnSpan,
                RowSpan = d.DefaultRowSpan,
                Visible = true,
                ShowTitle = true,
            });
        return list;
    }

    /// <summary>
    /// 与用户已保存的布局合并：保留用户的位置/尺寸，补上新版本新增的磁贴，
    /// 丢弃已不存在的磁贴。这样软件升级不会破坏既有布局。
    /// </summary>
    public static List<TileState> Merge(IEnumerable<TileState>? saved)
    {
        var result = new List<TileState>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextOrder = 0;

        foreach (var s in (saved ?? Enumerable.Empty<TileState>()).OrderBy(x => x.Order))
        {
            var def = Find(s.Id);
            if (def is null || !seen.Add(s.Id)) continue;   // 未知/重复磁贴直接忽略
            result.Add(new TileState
            {
                Id = s.Id,
                Order = nextOrder++,
                ColumnSpan = Math.Clamp(s.ColumnSpan, def.MinColumnSpan, def.MaxColumnSpan),
                RowSpan = Math.Clamp(s.RowSpan, def.MinRowSpan, def.MaxRowSpan),
                Visible = s.Visible,
                ShowTitle = s.ShowTitle,
            });
        }

        foreach (var d in Definitions)
        {
            if (seen.Contains(d.Id)) continue;
            result.Add(new TileState
            {
                Id = d.Id,
                Order = nextOrder++,
                ColumnSpan = d.DefaultColumnSpan,
                RowSpan = d.DefaultRowSpan,
                Visible = true,
                ShowTitle = true,
            });
        }

        return result;
    }

    /// <summary>把顺序重排为连续值，避免长期编辑后出现空档。</summary>
    public static void Reindex(List<TileState> tiles)
    {
        for (var i = 0; i < tiles.Count; i++) tiles[i].Order = i;
    }
}
