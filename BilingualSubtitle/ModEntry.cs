using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
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

        // 对话框：postfix 叠加中文
        DialogueBoxPatches.Initialize(this.Monitor, translationService);
        harmony.Patch(
            original: AccessTools.Method(typeof(DialogueBox), nameof(DialogueBox.draw), new[] { typeof(SpriteBatch) }),
            postfix: new HarmonyMethod(typeof(DialogueBoxPatches), nameof(DialogueBoxPatches.Draw_Postfix))
        );

        // 信件：构造 postfix（存英文副本+算中文分页）、draw prefix（中文模式切字体）+
        //       draw finalizer（恢复字体+画按钮）、receiveLeftClick / receiveKeyPress prefix
        //       （拦截切换按钮与 Z 键）
        LetterPatches.Initialize(this.Monitor, translationService);
        harmony.Patch(
            original: AccessTools.Constructor(typeof(LetterViewerMenu), new[] { typeof(string), typeof(string), typeof(bool) }),
            postfix: new HarmonyMethod(typeof(LetterPatches), nameof(LetterPatches.Ctor_Postfix))
        );
        harmony.Patch(
            original: AccessTools.Method(typeof(LetterViewerMenu), nameof(LetterViewerMenu.draw), new[] { typeof(SpriteBatch) }),
            prefix: new HarmonyMethod(typeof(LetterPatches), nameof(LetterPatches.Draw_Prefix)),
            finalizer: new HarmonyMethod(typeof(LetterPatches), nameof(LetterPatches.Draw_Finalizer))
        );
        // 切换按钮画在光标之前（patch 基类 drawMouse，类型过滤限信件菜单），
        // 保证光标不被按钮盖住（Deferred 排序按调用顺序，原版按钮都在光标前）
        harmony.Patch(
            original: AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.drawMouse)),
            prefix: new HarmonyMethod(typeof(LetterPatches), nameof(LetterPatches.DrawMouse_Prefix))
        );
        harmony.Patch(
            original: AccessTools.Method(typeof(LetterViewerMenu), nameof(LetterViewerMenu.receiveLeftClick), new[] { typeof(int), typeof(int), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(LetterPatches), nameof(LetterPatches.LeftClick_Prefix))
        );
        harmony.Patch(
            original: AccessTools.Method(typeof(LetterViewerMenu), nameof(LetterViewerMenu.receiveKeyPress), new[] { typeof(Keys) }),
            prefix: new HarmonyMethod(typeof(LetterPatches), nameof(LetterPatches.KeyPress_Prefix))
        );

#if DEBUG
        // ===== 测试辅助命令（仅 Debug 构建注册；发布用 Release 构建，命令天然不存在）=====
        // 游戏内聊天框的 /debug 会被 SMAPI 拦截（回复 "nice try"），SMAPI 控制台的 debug
        // 转发也不可用，故自注册命令，走 SMAPI 黑窗口输入。
        // console 命令在后台线程执行：把动作入队，借 UpdateTicked 在游戏主线程执行。
        this.Helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            while (_mainThreadQueue.TryDequeue(out var action))
                action();
        };
        this.Helper.ConsoleCommands.Add(
            "showmail",
            "直接打开指定信件：showmail <mailId>（id 用 mails 命令查）。注意会触发信件的 %item 副作用",
            this.OnShowMailCommand);
        this.Helper.ConsoleCommands.Add(
            "mails",
            "列出全部信件 mailId，可带关键词过滤：mails [filter]",
            this.OnMailsCommand);
#endif

        this.Monitor.Log("BilingualSubtitle loaded. DialogueBox.draw + LetterViewerMenu patched.", LogLevel.Info);
    }

#if DEBUG
    // console 线程 → 主线程 的动作队列（UpdateTicked 消费）
    private static readonly Queue<Action> _mainThreadQueue = new();

    private void OnShowMailCommand(string command, string[] args)
    {
        if (args.Length == 0)
        {
            this.Monitor.Log("用法：showmail <mailId> [重复次数1-5]。重复次数>1 时拼接正文制造多页长信（测翻页用）", LogLevel.Warn);
            return;
        }
        string mailId = args[0];
        int repeat = 1;
        if (args.Length > 1 && (!int.TryParse(args[1], out repeat) || repeat < 1))
            repeat = 1;
        repeat = Math.Min(repeat, 5);
        _mainThreadQueue.Enqueue(() =>
        {
            try
            {
                if (Game1.gameMode != 3)
                {
                    this.Monitor.Log("需要先载入存档（进入游戏）后才能打开信件。", LogLevel.Warn);
                    return;
                }
                string mail = DataLoader.Mail(Game1.content).GetValueOrDefault(mailId, "");
                if (string.IsNullOrEmpty(mail))
                {
                    this.Monitor.Log($"未找到 mailId '{mailId}'。用 mails 命令列出有效 id。", LogLevel.Warn);
                    return;
                }

                string text;
                if (repeat == 1)
                {
                    text = mail; // 原样打开（保留标题与 %item 命令，与游戏原生行为一致）
                }
                else
                {
                    // 多页测试：拼接正文。
                    // - 第一份保留 %item/%action（测交互物品），后续份剥掉（否则给 N 份物品/加 N 次任务）
                    // - 去掉 [#] 标题分隔（构造函数 mail.Split("[#]")[0] 会丢弃第一个 [#] 之后的全部内容）
                    string body = mail.Split("[#]")[0];
                    string bodyNoCmd = System.Text.RegularExpressions.Regex.Replace(body, @"%(?:item|action)[\s\S]*?%%", "");
                    text = string.Join("^^",
                        Enumerable.Repeat(body, 1).Concat(Enumerable.Repeat(bodyNoCmd, repeat - 1)));
                }

                var menu = new LetterViewerMenu(text, mailId);
                LetterPatches.DebugRepeatChinese(menu, mailId, repeat);
                Game1.activeClickableMenu = menu;
                this.Monitor.Log(
                    repeat > 1
                        ? $"已打开信件 '{mailId}'（正文重复 {repeat} 次的多页测试信；%item 只生效 1 次）"
                        : $"已打开信件 '{mailId}'（信件的 %item 副作用——给物品/加钱/加任务——会生效）",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"打开信件 '{mailId}' 失败：{ex}", LogLevel.Error);
            }
        });
    }

    private void OnMailsCommand(string command, string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        var keys = DataLoader.Mail(Game1.content).Keys
            .Where(k => filter == null || k.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .ToList();
        if (keys.Count == 0)
        {
            this.Monitor.Log($"无匹配 '{filter}' 的 mailId。", LogLevel.Info);
            return;
        }
        this.Monitor.Log($"共 {keys.Count} 个 mailId（{DataLoader.Mail(Game1.content).Count} 个总数）：", LogLevel.Info);
        // 每行 6 个，逗号分隔，避免刷 179 行
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < keys.Count; i++)
        {
            sb.Append(keys[i]);
            sb.Append(i % 6 == 5 || i == keys.Count - 1 ? "\n" : ", ");
        }
        this.Monitor.Log(sb.ToString(), LogLevel.Info);
    }
#endif
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
