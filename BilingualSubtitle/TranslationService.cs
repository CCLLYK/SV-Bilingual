using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace BilingualSubtitle;

/// <summary>
/// 加载英中对照翻译数据，按英文原文查中文译文。
/// 翻译文件位于 translations/dialogues.json，格式：{ "英文原文": "中文译文" }
/// </summary>
internal class TranslationService
{
    private readonly Dictionary<string, string> _translations = new();
    private readonly Dictionary<string, string> _mailTranslations = new();
    private readonly IMonitor _monitor;

    public TranslationService(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        var data = helper.Data.ReadJsonFile<Dictionary<string, string>>("translations/dialogues.json");
        if (data == null || data.Count == 0)
        {
            monitor.Log("未找到翻译数据 translations/dialogues.json，或文件为空。Mod 将只显示英文原文。", LogLevel.Warn);
        }
        else
        {
            foreach (var kv in data)
                _translations[kv.Key] = kv.Value;
            monitor.Log($"已加载 {_translations.Count} 条对话翻译", LogLevel.Info);
        }

        // 信件翻译：key=mailTitle，value=整封中文正文（含 ^ 换行与 @ 等占位符）
        var mailData = helper.Data.ReadJsonFile<Dictionary<string, string>>("translations/mails.json");
        if (mailData == null || mailData.Count == 0)
        {
            monitor.Log("未找到信件翻译数据 translations/mails.json，或文件为空。信件将不显示中文。", LogLevel.Warn);
        }
        else
        {
            foreach (var kv in mailData)
                _mailTranslations[kv.Key] = kv.Value;
            monitor.Log($"已加载 {_mailTranslations.Count} 封信件翻译", LogLevel.Info);
        }
    }

    /// <summary>查中文译文，没有匹配返回 null。</summary>
    public string? Get(string? english)
    {
        if (string.IsNullOrEmpty(english)) return null;

        // 先直接查
        if (_translations.TryGetValue(english, out var zh))
            return zh;

        // 反向替换占位符再查
        // 游戏显示时（Dialogue.ReplacePlayerEnteredStrings）把占位符替换成了实际值，
        // 这里把实际值换回占位符，和提取数据的 key 匹配。
        try
        {
            string normalized = english;
            var player = Game1.player;
            if (player != null)
            {
                // @ → 玩家名
                string? pName = player.displayName;
                if (!string.IsNullOrEmpty(pName) && normalized.Contains(pName))
                    normalized = normalized.Replace(pName, "@");

                // %farm → 农场名
                string farm = player.farmName.Value;
                if (!string.IsNullOrEmpty(farm) && normalized.Contains(farm))
                    normalized = normalized.Replace(farm, "%farm");

                // %spouse → 配偶名
                string? spouse = player.spouse;
                if (!string.IsNullOrEmpty(spouse))
                {
                    var npc = Game1.getCharacterFromName<StardewValley.NPC>(spouse);
                    if (npc != null)
                    {
                        string sName = npc.displayName;
                        if (!string.IsNullOrEmpty(sName) && normalized.Contains(sName))
                            normalized = normalized.Replace(sName, "%spouse");
                    }
                }

                // %kid1 / %kid2 → 孩子名
                var kids = player.getChildren();
                for (int i = 0; i < kids.Count && i < 2; i++)
                {
                    string kidName = kids[i].displayName;
                    if (!string.IsNullOrEmpty(kidName) && normalized.Contains(kidName))
                        normalized = normalized.Replace(kidName, i == 0 ? "%kid1" : "%kid2");
                }
            }

            if (normalized != english && _translations.TryGetValue(normalized, out zh))
                return zh;
        }
        catch
        {
            // 反向替换出错不影响游戏，静默跳过
        }

        return null;
    }

    /// <summary>
    /// 按 mailTitle 查整封信的中文译文。返回的文本含占位符（@ %farm %spouse 等），
    /// 调用方在显示前需做正向替换（与游戏构造英文信件时一致）。
    /// 无匹配返回 null。
    /// </summary>
    public string? GetMail(string? mailTitle)
    {
        if (string.IsNullOrEmpty(mailTitle))
            return null;
        return _mailTranslations.TryGetValue(mailTitle, out var zh) ? zh : null;
    }
}
