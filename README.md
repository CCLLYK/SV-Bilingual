# SV Bilingual

星露谷物语（Stardew Valley）双语对话字幕 Mod — NPC 对话框下方显示中文翻译。

## 功能

- NPC 对话英文原文下方叠加中文翻译
- 使用游戏自带的官方中文翻译（4717 条对话，覆盖 52 个 NPC 对话文件）
- 英文模式下正常游戏，中文字体自动切换显示
- 不修改任何游戏文件，纯 Harmony postfix 叠加绘制

## 安装

1. 安装 [SMAPI](https://smapi.io/) 4.0+（游戏版本需 1.6.14+）
2. 下载本 Mod 的 Release zip
3. 解压到 `Stardew Valley/Mods/` 目录，最终结构：
   ```
   Mods/BilingualSubtitle/
   ├── BilingualSubtitle.dll
   ├── manifest.json
   └── translations/dialogues.json
   ```
4. 通过 `StardewModdingAPI.exe` 启动游戏（不是 `Stardew Valley.exe`）
5. 游戏语言保持**英文**（Mod 会自动在英文下方叠加中文）

## 使用

装好后正常游戏即可。找 NPC 说话时，对话框下方会自动出现中文翻译。

部分对话未覆盖时会只显示英文原文，不影响游戏。

## 翻译数据

翻译来自游戏自带的官方中文语言包，通过提取脚本从 `Content/Characters/Dialogue/` 的英文和中文对照文件自动提取。

提取过程处理了对话数据里的各种标记（`#$b#` 分隔、`@` 玩家名、`^` 性别切换、`||` 多对话、`*` 分隔符等）。

## 技术实现

| 组件 | 说明 |
|---|---|
| **Harmony postfix** | 给 `DialogueBox.draw` 打 postfix，在游戏画完英文后叠加中文 |
| **字体切换** | 预加载游戏中文字体缓存，每帧绘制时临时切换 `SpriteText` 字体 + 语言代码，画完恢复 |
| **翻译查询** | 英文原文作 key 查 JSON，支持占位符反向替换（玩家名、配偶名、农场名、孩子名） |
| **去重缓存** | 对话内容不变时复用缓存翻译，避免每帧重复查询 |

## 构建

需要 .NET 6 SDK 和本地安装的星露谷 + SMAPI。

```bash
dotnet build BilingualSubtitle/BilingualSubtitle.csproj -c Debug
```

构建产物在 `bin/Debug/net6.0/`，将 `BilingualSubtitle.dll`、`manifest.json`、`translations/` 复制到游戏的 `Mods/BilingualSubtitle/` 目录即可。

重新提取翻译数据（需要游戏解包后的 Content 目录）：

```bash
python tools/extract_translations_template.**py**
```

## 已知限制

- 只覆盖 NPC 日常对话，**事件对话、信件、Strings 资源暂未覆盖**
- 问题对话框的选择选项暂未翻译
- 少量含 `%time` 占位符的对话可能不显示翻译（3 条，影响极小）

## 后续开发
### 开发
- 扩大翻译覆盖范围：事件对话（Events）、信件（Mail）、Strings 资源
- 问题对话框（isQuestion）的特殊处理：响应选项的翻译
- ......
### 优化
- 长文本换行优化：中文超出对话框宽度时的处理
- 位置适配：不同对话框尺寸/位置的坐标计算
- 配置菜单（GMCM）：开关、位置、字体大小、背景透明度
- ......

## 致谢

- **Pathoschild** — [SMAPI](https://github.com/Pathoschild/SMAPI) Mod 框架
- **ConcernedApe** — 星露谷物语及官方中文翻译
