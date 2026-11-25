using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace BilingualSubtitle;

public class ModEntry : Mod
{
    public override void Entry(IModHelper helper)
    {
        var translationService = new TranslationService(helper, this.Monitor);

        var harmony = new Harmony(this.ModManifest.UniqueID);
        DialogueBoxPatches.Initialize(this.Monitor, translationService);
        harmony.Patch(
            original: AccessTools.Method(typeof(DialogueBox), nameof(DialogueBox.draw), new[] { typeof(SpriteBatch) }),
            postfix: new HarmonyMethod(typeof(DialogueBoxPatches), nameof(DialogueBoxPatches.Draw_Postfix))
        );

        this.Monitor.Log("BilingualSubtitle loaded. DialogueBox.draw patched.", LogLevel.Info);
    }
}

internal static class DialogueBoxPatches
{
    private static IMonitor _monitor = null!;
    private static TranslationService? _translations;

    // draw 每帧调用(60fps)，用缓存避免重复处理：
    //   _lastDialogue      — 上一帧的对话文本，控制查翻译/log 只在内容变化时做
    //   _currentTranslation — 当前对话的中文译文，每帧绘制时复用
    private static string _lastDialogue = "";
    private static string? _currentTranslation = null;

    internal static void Initialize(IMonitor monitor, TranslationService translations)
    {
        _monitor = monitor;
        _translations = translations;
    }

    internal static void Draw_Postfix(DialogueBox __instance, SpriteBatch b)
    {
        try
        {
            // 过渡动画期间不处理：draw 方法在 transitioning 时走简化分支并 return，
            // 此时 getCurrentString() 可能返回 #$e# 后的内容（不属于当前对话句），
            // 会导致关闭过渡时"闪一下"显示不该出现的内容。对比测试确认原版无此现象。
            if (__instance.transitioning)
                return;

            var dialogue = __instance.characterDialogue;
            if (dialogue == null)
                return;

            string current = __instance.getCurrentString() ?? "";
            if (string.IsNullOrEmpty(current))
                return;

            // 对话内容变化时：查翻译 + log（每句话只做一次）
            if (current != _lastDialogue)
            {
                _lastDialogue = current;
                _currentTranslation = _translations?.Get(current);
                string speaker = dialogue.speaker?.displayName ?? "?";
                string tag = _currentTranslation != null ? $" → {_currentTranslation}" : " (无翻译)";
                _monitor.Log($"[hook] {speaker}: {current}{tag}", LogLevel.Debug);
            }

            // 每帧：有翻译就画中文（画面每帧重绘，必须每帧画）
            if (_currentTranslation == null)
                return;

            if (!FontCache.IsLoaded)
                FontCache.Load();

            // 在对话框下方画中文，留出边框/阴影距离
            int chineseX = __instance.x + 8;
            int chineseY = __instance.y + __instance.height + 12;
            int chineseWidth = __instance.width - 16;

            var snap = FontCache.SaveAndApply();
            try
            {
                // 算中文高度，画半透明背景条提高可读性
                int chineseHeight = SpriteText.getHeightOfString(_currentTranslation, chineseWidth);
                b.Draw(Game1.staminaRect,
                    new Rectangle(chineseX - 6, chineseY - 4, chineseWidth + 12, chineseHeight + 8),
                    Color.Black * 0.55f);
                // 画中文译文（白色，在半透明黑背景上清晰）
                SpriteText.drawString(b, _currentTranslation, chineseX, chineseY,
                    width: chineseWidth, color: Color.White);
            }
            finally
            {
                FontCache.Restore(snap);
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed in {nameof(Draw_Postfix)}:\n{ex}", LogLevel.Error);
        }
    }
}
