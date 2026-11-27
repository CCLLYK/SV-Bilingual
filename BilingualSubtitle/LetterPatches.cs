using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace BilingualSubtitle;

/// <summary>
/// 信件（LetterViewerMenu）双语切换：默认显示英文原文，按切换按钮或快捷键 Z 切到整封中文。
///
/// 为什么不像对话框那样“下方叠加”：信件文本区几乎占满整张信纸（宽 width-64，高 height-128），
/// 长信件每页都铺满，无处塞中文。所以采用“整信替换 + 一键切换语言”。
///
/// 实现要点：
/// - 构造 Postfix：存英文 mailMessage 副本，查 GetMail 得中文整段，做占位符正向替换后
///   用同样的 getStringBrokenIntoSectionsOfHeight 切中文分页（度量时临时切中文字体）。
/// - draw Prefix：中文模式下临时把 SpriteText 切到中文字体 + zh 语言代码（SaveAndApply），
///   让游戏自己的 draw 用中文渲染管线画 mailMessage——中文直接落在信纸纹理上，
///   与官方中文版显示一致，无覆盖层、无色差，视觉无缝。
/// - draw Finalizer：恢复字体（原方法正常结束或抛异常都会执行，保证不泄漏字体状态），
///   然后画切换按钮（dialogueFont 矢量字体，大小随文字自适应）。
/// - receiveLeftClick Prefix / receiveKeyPress Prefix：拦截切换按钮点击与 Z 键。
///
/// 交互物品（任务/道具）的显示时机由游戏 ShouldShowInteractable() 自动按“当前语言最后一页”判断，
/// 切换语言后页数变化会自动适应，无需特殊处理。
/// </summary>
internal static class LetterPatches
{
    private static IMonitor _monitor = null!;
    private static TranslationService? _translations;

    private enum LangMode { English, Chinese }

    /// <summary>每个信件实例的附加状态。用 ConditionalWeakTable 挂载，信件销毁时自动回收。</summary>
    private class LetterState
    {
        public List<string> EnglishPages = new();
        public List<string> ChinesePages = new();
        public LangMode Mode = LangMode.English;
        public bool HasTranslation => ChinesePages.Count > 0;
    }

    private static readonly ConditionalWeakTable<LetterViewerMenu, LetterState> _states = new();

    // 切换快捷键。
    // 注意不能用 T：它是游戏默认的聊天键（Options.chatButton = T 和 /），
    // Game1 在菜单之前就把 T 消费成“打开聊天框”，并把按键状态清空，菜单根本收不到。
    // Z 不在游戏默认绑定里。选键前需查 Options.cs 的默认 InputButton 列表。
    private const Keys ToggleKey = Keys.Z;

    internal static void Initialize(IMonitor monitor, TranslationService translations)
    {
        _monitor = monitor;
        _translations = translations;
    }

    // === 切换按钮：照抄游戏原生「接受任务」按钮的模式 ===
    // drawTextureBox 画九宫格框 + Game1.dialogueFont（矢量字体）写字。
    // 不用 SpriteText 画按钮文字：它没有 scale 参数，字号固定 3 倍像素，"CN" 约 48x24px，
    // 小按钮装不下（第一版溢出的根因）。dialogueFont 可用 MeasureString 精确量尺寸，
    // 按钮框随文字自适应，观感与 acceptQuestButton 一致。

    private static string GetButtonLabel(LetterState st)
    {
        // 英文模式显示 CN（提示可切中文），中文模式显示 EN（提示可切英文）
        return st.Mode == LangMode.English ? "CN" : "EN";
    }

    /// <summary>按钮位置：backButton 右侧紧邻，与 back/forward 底行垂直居中；尺寸随文字自适应。</summary>
    private static Rectangle GetSwitchButtonBounds(LetterViewerMenu menu, string label)
    {
        Vector2 textSize = Game1.dialogueFont.MeasureString(label);
        int w = (int)textSize.X + 24;
        int h = (int)textSize.Y + 20;
        // backButton 在 x+32, y+height-32-64，宽48；切换按钮放其右侧 8px
        int x = menu.xPositionOnScreen + 32 + 48 + 8;
        int y = menu.yPositionOnScreen + menu.height - 32 - 64 + (44 - h) / 2;
        return new Rectangle(x, y, w, h);
    }

    private static LetterState GetOrCreateState(LetterViewerMenu menu)
    {
        return _states.GetOrCreateValue(menu);
    }

    /// <summary>
    /// 构造 Postfix：仅处理真正的信件（isMail && mailTitle 非空）。
    /// 此时游戏已把英文 mailMessage 分好页。存英文副本，算中文分页。
    /// </summary>
    internal static void Ctor_Postfix(LetterViewerMenu __instance, string mailTitle)
    {
        try
        {
            if (!__instance.isMail || string.IsNullOrEmpty(mailTitle))
                return;

            var st = GetOrCreateState(__instance);
            st.EnglishPages = new List<string>(__instance.mailMessage);

            string? zh = _translations?.GetMail(mailTitle);
            if (string.IsNullOrEmpty(zh))
            {
                _monitor.Log($"[mail] 无中文翻译：{mailTitle}", LogLevel.Debug);
                return;
            }

            // 占位符正向替换（与游戏构造英文信件时一致）
            zh = ApplyPlaceholders(zh, __instance);

            // 算中文分页：必须用中文字体度量，否则高度算错（英文 sprite sheet 度量中文不准）
            if (!FontCache.IsLoaded)
                FontCache.Load();

            int pageHeight = __instance.height - 128;
            if (__instance.HasInteractable())
                pageHeight -= 32;

            var snap = FontCache.SaveAndApply();
            try
            {
                st.ChinesePages = SpriteText.getStringBrokenIntoSectionsOfHeight(
                    zh, __instance.width - 64, pageHeight);
            }
            finally
            {
                FontCache.Restore(snap);
            }

            _monitor.Log($"[mail] 就绪：{mailTitle}（英 {st.EnglishPages.Count} 页 / 中 {st.ChinesePages.Count} 页）", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor.Log($"LetterPatches Ctor_Postfix 失败：\n{ex}", LogLevel.Error);
        }
    }

    /// <summary>对中文译文做占位符正向替换（@→玩家名、%farm→农场名、%spouse→配偶名 等）。</summary>
    private static string ApplyPlaceholders(string zh, LetterViewerMenu menu)
    {
        var player = Game1.player;
        if (player == null)
            return zh;

        // @ → 玩家名
        zh = zh.Replace("@", player.Name);

        // 性别切换块（^ 在信件里是换行，altTokenOnly:true 只处理 ^token 形式，普通 ^ 保留为换行）
        zh = Dialogue.applyGenderSwitch(player.Gender, zh, altTokenOnly: true);

        // %farm → 农场名
        string farm = player.farmName.Value;
        if (!string.IsNullOrEmpty(farm))
            zh = zh.Replace("%farm", farm);

        // %spouse → 配偶名
        string? spouse = player.spouse;
        if (!string.IsNullOrEmpty(spouse))
        {
            var npc = Game1.getCharacterFromName<NPC>(spouse);
            if (npc != null)
                zh = zh.Replace("%spouse", npc.displayName);
        }

        // %kid1 / %kid2 → 孩子名
        var kids = player.getChildren();
        if (kids.Count > 0)
            zh = zh.Replace("%kid1", kids[0].displayName);
        if (kids.Count > 1)
            zh = zh.Replace("%kid2", kids[1].displayName);

        // %secretsanta（与游戏构造一致：非冬季礼物节期间显示 ???）
        bool hideSecretSanta = menu.isFromCollection
            && ((int)Game1.season != 3 || Game1.dayOfMonth < 18 || Game1.dayOfMonth > 25);
        zh = zh.Replace("%secretsanta",
            hideSecretSanta ? "???" : Utility.GetRandomWinterStarParticipant().displayName);

        return zh;
    }

    /// <summary>
    /// draw Prefix：中文模式下切到中文字体，让游戏自己的 draw 画中文（无缝，无覆盖层）。
    /// 字体快照通过 __state 传给 Finalizer（原方法抛异常时也会传递）。
    /// </summary>
    internal static void Draw_Prefix(LetterViewerMenu __instance, out FontSnapshot? __state)
    {
        __state = null;
        try
        {
            if (!_states.TryGetValue(__instance, out var st))
                return;
            if (st.Mode != LangMode.Chinese || !st.HasTranslation)
                return;
            if (__instance.scale < 1f)
                return; // 开信动画期间不切字体

            if (!FontCache.IsLoaded)
                FontCache.Load();
            __state = FontCache.SaveAndApply();
        }
        catch (Exception ex)
        {
            _monitor.Log($"LetterPatches Draw_Prefix 失败：\n{ex}", LogLevel.Error);
        }
    }

    /// <summary>
    /// draw Finalizer：恢复字体状态（原方法正常结束或抛异常都会执行，保证不泄漏）。
    /// 注意：切换按钮不在这里画！光标 drawMouse 在游戏 draw 末尾调用，本方法在其后执行，
    /// Deferred 排序下（SpriteSortMode.Deferred，按调用顺序）按钮会盖住光标。
    /// 按钮改在 DrawMouse_Prefix（光标绘制之前）画。
    /// </summary>
    internal static void Draw_Finalizer(LetterViewerMenu __instance, FontSnapshot? __state)
    {
        if (__state != null)
            FontCache.Restore(__state);
    }

    /// <summary>
    /// drawMouse Prefix：在光标绘制之前画切换按钮，与原版按钮同级、位于光标下层。
    /// patch 在基类 IClickableMenu 上（LetterViewerMenu 未 override drawMouse），类型过滤限定信件菜单。
    /// 已知边界：手柄 SnappyMenus 且无可见按钮时游戏跳过 drawMouse，按钮也不画（键鼠玩家不受影响）。
    /// </summary>
    internal static void DrawMouse_Prefix(IClickableMenu __instance, SpriteBatch b)
    {
        try
        {
            if (__instance is not LetterViewerMenu menu)
                return;
            if (!_states.TryGetValue(menu, out var st))
                return;
            if (!st.HasTranslation || menu.scale < 1f)
                return;
            DrawSwitchButton(menu, st, b);
        }
        catch (Exception ex)
        {
            _monitor.Log($"LetterPatches DrawMouse_Prefix 失败：\n{ex}", LogLevel.Error);
        }
    }

    private static void DrawSwitchButton(LetterViewerMenu menu, LetterState st, SpriteBatch b)
    {
        string label = GetButtonLabel(st);
        var bounds = GetSwitchButtonBounds(menu, label);
        // 悬停判定显式用 zoom 坐标系（ui_scale:false），与游戏传给 receiveLeftClick 的坐标同源。
        // 无参版在渲染阶段走 uiMode=true（除 uiScale），与输入阶段（除 zoomLevel）不同源，
        // 两套缩放设置不一致时判定区会偏移。
        bool hover = bounds.Contains(Game1.getOldMouseX(ui_scale: false), Game1.getOldMouseY(ui_scale: false));

        // 与游戏 acceptQuestButton 同款：mouseCursors 九宫格框，hover 时粉色
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(403, 373, 9, 9),
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            hover ? Color.LightPink : Color.White, 4f);
        Utility.drawTextWithShadow(b, label, Game1.dialogueFont,
            new Vector2(bounds.X + 12, bounds.Y + 12), Game1.textColor);
    }

    /// <summary>receiveLeftClick Prefix：拦截切换按钮点击。</summary>
    internal static bool LeftClick_Prefix(LetterViewerMenu __instance, int x, int y)
    {
        try
        {
            if (_states.TryGetValue(__instance, out var st) && st.HasTranslation)
            {
                if (GetSwitchButtonBounds(__instance, GetButtonLabel(st)).Contains(x, y))
                {
                    Toggle(__instance, st);
                    return false; // 拦截，不执行原 receiveLeftClick
                }
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"LetterPatches LeftClick_Prefix 失败：\n{ex}", LogLevel.Error);
        }
        return true; // 放行原方法
    }

    /// <summary>receiveKeyPress Prefix：拦截 Z 键切换。</summary>
    internal static bool KeyPress_Prefix(LetterViewerMenu __instance, Keys key)
    {
        try
        {
            if (key == ToggleKey && _states.TryGetValue(__instance, out var st) && st.HasTranslation)
            {
                Toggle(__instance, st);
                return false; // 拦截切换键，不传给原方法
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"LetterPatches KeyPress_Prefix 失败：\n{ex}", LogLevel.Error);
        }
        return true; // 其他键放行
    }

    /// <summary>切换语言：交换 mailMessage（英文副本 ⇄ 中文副本），重置到第一页。</summary>
    private static void Toggle(LetterViewerMenu menu, LetterState st)
    {
        if (st.Mode == LangMode.English)
        {
            // 切到中文：确保英文副本已存（保险重存一次），替换为中文
            if (st.EnglishPages.Count == 0)
                st.EnglishPages = new List<string>(menu.mailMessage);
            menu.mailMessage = new List<string>(st.ChinesePages);
            st.Mode = LangMode.Chinese;
        }
        else
        {
            menu.mailMessage = new List<string>(st.EnglishPages);
            st.Mode = LangMode.English;
        }
        menu.page = 0;
        menu.OnPageChange();
        Game1.playSound("shwip");
        _monitor.Log($"[mail] 切换到 {st.Mode}（{menu.mailMessage.Count} 页）", LogLevel.Debug);
    }

#if DEBUG
    /// <summary>
    /// 测试辅助（配合 showmail &lt;id&gt; &lt;repeat&gt; 命令）：把信件中文译文重复 repeat 次
    /// 重新分页，配合英文侧的重复正文，制造多页长信以测试翻页/切换/交互物品时机。
    /// </summary>
    internal static void DebugRepeatChinese(LetterViewerMenu menu, string mailId, int repeat)
    {
        try
        {
            if (repeat <= 1 || _translations == null || !_states.TryGetValue(menu, out var st))
                return;
            string? zh = _translations.GetMail(mailId);
            if (string.IsNullOrEmpty(zh))
                return;
            zh = ApplyPlaceholders(zh, menu);
            zh = string.Join("^^", Enumerable.Repeat(zh, repeat));

            if (!FontCache.IsLoaded)
                FontCache.Load();
            int pageHeight = menu.height - 128;
            if (menu.HasInteractable())
                pageHeight -= 32;
            var snap = FontCache.SaveAndApply();
            try
            {
                st.ChinesePages = SpriteText.getStringBrokenIntoSectionsOfHeight(zh, menu.width - 64, pageHeight);
            }
            finally
            {
                FontCache.Restore(snap);
            }
            _monitor.Log($"[mail] debug：中文重复 {repeat} 次 → {st.ChinesePages.Count} 页", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor.Log($"DebugRepeatChinese 失败：\n{ex}", LogLevel.Error);
        }
    }
#endif
}
