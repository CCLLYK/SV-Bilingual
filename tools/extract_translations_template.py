#!/usr/bin/env python3
"""
从星露谷游戏的对话与信件数据中提取英中对照翻译。

前置条件：
  需要先用解包工具（如 StardewXnbHack）把游戏的 Content 目录解包，
  本脚本读取的是解包后的 .json 文件，不能直接读 .xnb。

原理：
  1. 对话：Characters/Dialogue 下按语言分文件存储，如 Evelyn.json（英文）和
     Evelyn.zh-CN.json（中文）。两个文件 key 相同，value 是带命令标记的对话文本。
     按 #$b# / #$e# 分隔符拆分多句对话，去掉命令标记（$h $s 等），
     生成 { "英文原文": "中文译文" } 的对照表。
  2. 信件：Data/mail.json（英文）与 mail.zh-CN.json（中文）按 mailTitle 对齐，
     剥掉 %item/%action/[#] 等命令与格式标记（^ 换行符保留），
     生成 { "mailTitle": "整封中文正文" }。

用法：
  1. 修改下面配置区的 4 个路径（DIALOGUE_DIR / OUTPUT_FILE / MAIL_DIR / MAIL_OUTPUT_FILE）
  2. python extract_translations_template.py
"""

import json
import os
import random
import re

# ===== 配置（使用前必改）=====

# 游戏对话数据目录（位于解包后的 Content 目录下）
# Windows 示例：r"D:\Stardew Valley\Content (unpacked)\Characters\Dialogue"
# macOS / Linux 示例：r"/path/to/Stardew Valley/Content (unpacked)/Characters/Dialogue"
DIALOGUE_DIR = r"your_path"

# 对话输出文件路径，所在目录不存在会自动创建；只写文件名则输出到当前目录
# Windows 示例：r"D:\Stardew Valley\BilingualSubtitle\translations\dialogues.json"
# macOS / Linux 示例：r"/path/to/BilingualSubtitle/translations/dialogues.json"
OUTPUT_FILE = r"your_path"

# 信件数据目录（位于解包后的 Content 目录下，包含 mail.json 与 mail.zh-CN.json）
# Windows 示例：r"D:\Stardew Valley\Content (unpacked)\Data"
# macOS / Linux 示例：r"/path/to/Stardew Valley/Content (unpacked)/Data"
MAIL_DIR = r"your_path"

# 信件输出文件路径，所在目录不存在会自动创建；只写文件名则输出到当前目录
# Windows 示例：r"D:\Stardew Valley\BilingualSubtitle\translations\mails.json"
# macOS / Linux 示例：r"/path/to/BilingualSubtitle/translations/mails.json"
MAIL_OUTPUT_FILE = r"your_path"

# ===== 正则（对话）=====

# 对话分隔符：#$b#（对话分隔）和 #$e#（对话结束+继续）
# 用非捕获组 (?:b|e) 避免 re.split 把 b/e 放进结果
# 游戏里这些会把一段对话拆成多句，每句单独显示
SPLIT_RE = re.compile(r'#\$(?:b|e)#')

# 清理标记（按顺序执行）：
# 1. #$...# 形式：#$1 key#（引用）、#$q ...#（问题）、#$r ...#（响应）等
HASH_CMD_RE = re.compile(r'#\$\w+(?:\s+[^\s#]+)*#')
# 2. $xxx 形式：$h（happy）、$s（sad）、$u（unique）、$k（kill）、$9 等（含数字标记）
DOLLAR_CMD_RE = re.compile(r'\$[a-zA-Z0-9]+')
# 3. %xxx 命令标记（%noturn 等），但保留已知占位符（游戏运行时替换成实际值）
#    占位符：%spouse %farm %time %kid1 %kid2 %firstnameletter %favorite
PERCENT_CMD_RE = re.compile(r'%(?!spouse|farm|time|kid1|kid2|firstnameletter|favorite)[a-zA-Z0-9]+')

# 语言后缀（跳过非英文文件）
LANG_SUFFIX_RE = re.compile(
    r'\.(zh-CN|de-DE|es-ES|fr-FR|hu-HU|it-IT|ja-JP|ko-KR|pt-BR|ru-RU|tr-TR)\.json$'
)

# ===== 正则（信件）=====
# 信件文本（mail.json）与对话（Dialogue）标记体系不同：
#   - ^ 在信件里是换行符（Dialogue 里是性别切换），必须保留
#   - @ 是玩家名占位符，保留（运行时由 Mod 替换为 Game1.player.Name）
#   - %item ... %% / %action ... %% 成对命令，整段剥掉（游戏构造信件时已消费）
#   - [#] 后是信件标题，取 [0] 即正文
#   - [letterbg n] / [textcolor x] 等方括号格式标签，剥掉
#   - 独立 %xxx 命令（%noturn 等）剥掉，但保留占位符白名单

# %item ... %% 成对（非贪婪，跨行 [\s\S]）
MAIL_ITEM_RE = re.compile(r'%item[\s\S]*?%%')
# %action ... %% 成对
MAIL_ACTION_RE = re.compile(r'%action[\s\S]*?%%')
# 方括号格式标签：[letterbg 2] / [textcolor red] 等
MAIL_TAG_RE = re.compile(r'\[[a-zA-Z]+[^\]]*\]')
# 独立 % 命令，保留占位符白名单（信件里可能出现 %farm %spouse %kid1 %kid2 %time %secretsanta 等）
MAIL_PERCENT_CMD_RE = re.compile(
    r'%(?!spouse|farm|time|kid1|kid2|firstnameletter|favorite|secretsanta|season|year)[a-zA-Z0-9]+'
)
# 残余的 %% （理论上已被成对正则带走，兜底）
MAIL_DOUBLE_PERCENT_RE = re.compile(r'%%')


def clean_text(text):
    """去掉对话命令标记，返回纯文本。模拟 getCurrentString() 的处理。"""
    text = HASH_CMD_RE.sub('', text)
    text = DOLLAR_CMD_RE.sub('', text)
    text = PERCENT_CMD_RE.sub('', text)
    return text.strip()


def clean_mail_text(text):
    """清理信件中文文本：剥掉命令/格式标记，保留可读正文 + 换行(^) + 占位符(@ %farm 等)。"""
    text = text.split('[#]')[0]        # 取正文（[#] 后是标题）
    text = MAIL_ITEM_RE.sub('', text)   # 剥 %item ... %%
    text = MAIL_ACTION_RE.sub('', text) # 剥 %action ... %%
    text = MAIL_TAG_RE.sub('', text)    # 剥 [letterbg]/[textcolor] 等
    text = MAIL_PERCENT_CMD_RE.sub('', text)  # 剥独立 %cmd，保留占位符
    text = MAIL_DOUBLE_PERCENT_RE.sub('', text)  # 兜底残余 %%
    return text.strip()


def extract_pairs(en_value, zh_value):
    """从一对英文/中文对话 value 提取英中对照。按分隔符拆分多句。"""
    # 先按 || 拆分多对话（multipleDialogueDelineator，游戏随机或按条件选一个）
    en_multi = en_value.split('||')
    zh_multi = zh_value.split('||')

    pairs = []
    if len(en_multi) == len(zh_multi):
        for en_m, zh_m in zip(en_multi, zh_multi):
            pairs.extend(_extract_single(en_m, zh_m))
    else:
        # || 数不匹配，整体处理
        pairs.extend(_extract_single(en_value, zh_value))
    return pairs


def _extract_single(en_value, zh_value):
    """单个对话（已按 || 拆分后）的提取。"""
    # 游戏在 Dialogue.cs 703 行把 * 替换成 #$b#（分隔符），这里同步处理
    en_value = en_value.replace('*', '#$b#')
    zh_value = zh_value.replace('*', '#$b#')

    # 按 ^ 拆分性别版本（^ 前是男版，后是女版）
    en_genders = en_value.split('^')
    zh_genders = zh_value.split('^')

    pairs = []
    if len(en_genders) == len(zh_genders):
        for en_g, zh_g in zip(en_genders, zh_genders):
            pairs.extend(_split_sentences(en_g, zh_g))
    else:
        pairs.extend(_split_sentences(en_value, zh_value))
    return pairs


def _split_sentences(en_value, zh_value):
    """按 #$b# / #$e# 拆分句子。"""
    en_parts = SPLIT_RE.split(en_value)
    zh_parts = SPLIT_RE.split(zh_value)

    pairs = []
    if len(en_parts) == len(zh_parts):
        for en, zh in zip(en_parts, zh_parts):
            en_clean = clean_text(en)
            zh_clean = clean_text(zh)
            if en_clean and zh_clean:
                pairs.append((en_clean, zh_clean))
    else:
        en_clean = clean_text(en_value)
        zh_clean = clean_text(zh_value)
        if en_clean and zh_clean:
            pairs.append((en_clean, zh_clean))
    return pairs


def extract_mails():
    """提取信件中文译文：按 mailTitle 对齐 mail.json(英) 与 mail.zh-CN.json(中)，
    只保留中文译文（清理命令标记后），输出 { mailTitle: 整封中文正文 }。
    英文原文不存（运行时游戏 mailMessage 即英文，Mod 只替换成中文）。"""
    en_path = os.path.join(MAIL_DIR, 'mail.json')
    zh_path = os.path.join(MAIL_DIR, 'mail.zh-CN.json')
    if not os.path.exists(en_path) or not os.path.exists(zh_path):
        print(f'\n[mail] 跳过：未找到 mail.json / mail.zh-CN.json 于 {MAIL_DIR}')
        return

    with open(en_path, 'r', encoding='utf-8') as f:
        en = json.load(f)
    with open(zh_path, 'r', encoding='utf-8') as f:
        zh = json.load(f)

    output = {}
    skipped = 0
    for k, en_v in en.items():
        if not isinstance(en_v, str):
            skipped += 1
            continue
        zh_v = zh.get(k)
        if not isinstance(zh_v, str):
            skipped += 1
            continue
        zh_clean = clean_mail_text(zh_v)
        if zh_clean:
            output[k] = zh_clean

    # 写输出（MAIL_OUTPUT_FILE 不含目录部分时 dirname 为空，跳过创建）
    mail_out_dir = os.path.dirname(MAIL_OUTPUT_FILE)
    if mail_out_dir:
        os.makedirs(mail_out_dir, exist_ok=True)
    with open(MAIL_OUTPUT_FILE, 'w', encoding='utf-8') as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    print(f'\n[mail] 提取完成：{len(output)} 封信件中文译文（跳过 {skipped}）')
    print(f'  输出：{MAIL_OUTPUT_FILE}')
    # 随机抽样，方便人工抽查提取结果
    if output:
        sample_n = min(2, len(output))
        for k, v in random.sample(list(output.items()), sample_n):
            print(f'  样本 [{k}]:')
            print(f'    {v[:120]}')


def main():
    # 路径预检查：给出可读的提示，而不是抛出堆栈
    all_paths = {
        'DIALOGUE_DIR': DIALOGUE_DIR,
        'OUTPUT_FILE': OUTPUT_FILE,
        'MAIL_DIR': MAIL_DIR,
        'MAIL_OUTPUT_FILE': MAIL_OUTPUT_FILE,
    }
    unfilled = [name for name, value in all_paths.items() if value == 'your_path']
    if unfilled:
        raise SystemExit(
            "请先修改脚本顶部配置区的路径再运行，仍为占位符的有："
            + ", ".join(unfilled)
        )
    if not os.path.isdir(DIALOGUE_DIR):
        raise SystemExit(
            f"对话目录不存在：{DIALOGUE_DIR}\n"
            "请确认路径填写正确，且已用解包工具把游戏 Content 目录解包为 .json 文件。"
        )

    output = {}
    stats = {'files': 0, 'pairs': 0, 'skipped': 0}

    for filename in sorted(os.listdir(DIALOGUE_DIR)):
        if not filename.endswith('.json'):
            continue
        if LANG_SUFFIX_RE.search(filename):
            continue  # 跳过非英文文件

        en_path = os.path.join(DIALOGUE_DIR, filename)
        zh_path = os.path.join(DIALOGUE_DIR, filename.replace('.json', '.zh-CN.json'))

        if not os.path.exists(zh_path):
            stats['skipped'] += 1
            continue

        with open(en_path, 'r', encoding='utf-8') as f:
            en_data = json.load(f)
        with open(zh_path, 'r', encoding='utf-8') as f:
            zh_data = json.load(f)

        stats['files'] += 1

        for key in en_data:
            if key not in zh_data:
                continue
            en_value = en_data[key]
            zh_value = zh_data[key]

            if not isinstance(en_value, str) or not isinstance(zh_value, str):
                continue

            for en_clean, zh_clean in extract_pairs(en_value, zh_value):
                if en_clean not in output:  # 去重（保留第一条）
                    output[en_clean] = zh_clean
                    stats['pairs'] += 1

    # 写输出（OUTPUT_FILE 不含目录部分时 dirname 为空，跳过创建）
    out_dir = os.path.dirname(OUTPUT_FILE)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(OUTPUT_FILE, 'w', encoding='utf-8') as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    print(f"提取完成：")
    print(f"  处理文件：{stats['files']} 个（跳过无中文：{stats['skipped']}）")
    print(f"  提取翻译：{stats['pairs']} 条")
    print(f"  输出：{OUTPUT_FILE}")

    # 随机抽样，方便人工抽查提取结果
    if output:
        sample_n = min(3, len(output))
        print(f"\n随机抽样（{sample_n} 条）：")
        for en, zh in random.sample(list(output.items()), sample_n):
            print(f"  {en}")
            print(f"    -> {zh}")

    # 信件翻译提取（MAIL_DIR 不存在时会在函数内友好提示并跳过，不影响对话提取）
    extract_mails()


if __name__ == '__main__':
    main()
