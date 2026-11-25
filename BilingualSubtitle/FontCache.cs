using System.Collections.Generic;
using System.Reflection;
using BmFont;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;

namespace BilingualSubtitle;

/// <summary>
/// 预加载中文字体数据并缓存。Mod 启动后只加载一次，
/// 后续每帧绘制时只做 SpriteText 静态字段 + 语言代码的引用交换，零纹理加载开销。
/// </summary>
internal static class FontCache
{
    public static FontFile? ChineseFontFile;
    public static List<Texture2D> ChineseFontPages = null!;
    public static Dictionary<char, FontChar> ChineseCharacterMap = null!;
    public static float ChineseFontPixelZoom = 1.5f; // 中文标准 zoom，见 LoadFontData case zh
    public static bool IsLoaded;

    // LocalizedContentManager._currentLangCode 的反射缓存（只查一次）
    private static FieldInfo? _langCodeField;
    internal static FieldInfo? GetLangCodeField()
    {
        if (_langCodeField == null)
            _langCodeField = typeof(LocalizedContentManager)
                .GetField("_currentLangCode", BindingFlags.NonPublic | BindingFlags.Static)!;
        return _langCodeField!;
    }

    public static void Load()
    {
        if (IsLoaded) return;

        // loadFont 是 SpriteText 的 private 方法，内部用 FontLoader.Parse 解析 .fnt。
        // 用反射调用一次，避开 XmlSource 类型（游戏内部类型，不公开）的依赖问题。
        var loadFont = typeof(SpriteText).GetMethod("loadFont",
            BindingFlags.NonPublic | BindingFlags.Static);
        ChineseFontFile = (FontFile)loadFont!.Invoke(null, new object[] { "Fonts\\Chinese" })!;

        // 构建字符映射表：char → FontChar（含 UV、尺寸、所在纹理页）
        ChineseCharacterMap = new Dictionary<char, FontChar>();
        foreach (var c in ChineseFontFile.Chars)
            ChineseCharacterMap[(char)c.ID] = c;

        // 加载纹理页：Chinese_0.xnb ~ Chinese_3.xnb
        ChineseFontPages = new List<Texture2D>();
        foreach (var page in ChineseFontFile.Pages)
            ChineseFontPages.Add(Game1.content.Load<Texture2D>("Fonts\\" + page.File));

        IsLoaded = true;
    }

    /// <summary>
    /// 保存当前字体状态 + 语言代码，然后切换到中文字体 + zh 语言。
    /// 返回快照供 Restore 使用。每帧调用（仅引用交换 + 一次反射 SetValue，开销很小）。
    ///
    /// 为什么还要切语言代码：SpriteText.drawString 在英文模式下走 sprite sheet 字体路径，
    /// 根本不看 FontFile/characterMap。只有 CurrentLanguageCode=zh（非拉丁）时才走 BMFont 路径。
    /// 直接反射改 _currentLangCode 字段，绕过 CurrentLanguageCode 的 setter，
    /// 避免触发 OnLanguageChange 事件（那会导致游戏重新加载内容）。
    /// </summary>
    public static FontSnapshot SaveAndApply()
    {
        var snap = new FontSnapshot
        {
            FontFile = SpriteText.FontFile,
            FontPages = SpriteText.fontPages,
            CharacterMap = SpriteText.characterMap,
            FontPixelZoom = SpriteText.fontPixelZoom,
            LanguageCode = (LocalizedContentManager.LanguageCode)GetLangCodeField().GetValue(null)!
        };
        // 交换字体数据
        SpriteText.FontFile = ChineseFontFile;
        SpriteText.fontPages = ChineseFontPages;
        SpriteText.characterMap = ChineseCharacterMap;
        SpriteText.fontPixelZoom = ChineseFontPixelZoom;
        // 临时切语言为 zh（绕过 setter，不触发 OnLanguageChange）
        GetLangCodeField().SetValue(null, LocalizedContentManager.LanguageCode.zh);
        return snap;
    }

    /// <summary>恢复 SaveAndApply 之前的字体状态和语言代码。</summary>
    public static void Restore(FontSnapshot snap)
    {
        SpriteText.FontFile = snap.FontFile;
        SpriteText.fontPages = snap.FontPages;
        SpriteText.characterMap = snap.CharacterMap;
        SpriteText.fontPixelZoom = snap.FontPixelZoom;
        GetLangCodeField().SetValue(null, snap.LanguageCode);
    }
}

internal class FontSnapshot
{
    public FontFile? FontFile;
    public List<Texture2D>? FontPages;
    public Dictionary<char, FontChar>? CharacterMap;
    public float FontPixelZoom;
    public LocalizedContentManager.LanguageCode LanguageCode;
}
