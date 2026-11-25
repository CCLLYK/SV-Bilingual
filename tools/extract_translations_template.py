#!/usr/bin/env python3
"""
从星露谷游戏的对话数据中提取英中对照翻译。

前置条件：
  需要先用解包工具（如 StardewXnbHack）把游戏的 Content 目录解包，
  本脚本读取的是解包后的 .json 文件，不能直接读 .xnb。

原理：
  游戏的对话数据按语言分文件存储，如 Evelyn.json（英文）和 Evelyn.zh-CN.json（中文）。
  两个文件 key 相同，value 是带命令标记的对话文本。
  本脚本按 #$b# / #$e# 分隔符拆分多句对话，去掉命令标记（$h $s 等），
  生成 { "英文原文": "中文译文" } 的对照表。

用法：
  1. 修改下面 DIALOGUE_DIR 和 OUTPUT_FILE 两个路径
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

# 输出文件路径，所在目录不存在会自动创建；只写文件名则输出到当前目录
# Windows 示例：r"D:\Stardew Valley\BilingualSubtitle\translations\dialogues.json"
# macOS / Linux 示例：r"/path/to/BilingualSubtitle/translations/dialogues.json"
OUTPUT_FILE = r"your_path"

# ===== 正则 =====

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


def clean_text(text):
    """去掉对话命令标记，返回纯文本。模拟 getCurrentString() 的处理。"""
    text = HASH_CMD_RE.sub('', text)
    text = DOLLAR_CMD_RE.sub('', text)
    text = PERCENT_CMD_RE.sub('', text)
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


def main():
    # 路径预检查：给出可读的提示，而不是抛出堆栈
    if DIALOGUE_DIR == 'your_path' or OUTPUT_FILE == 'your_path':
        raise SystemExit(
            "请先修改脚本顶部的 DIALOGUE_DIR 和 OUTPUT_FILE 两个路径，再运行本脚本。"
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


if __name__ == '__main__':
    main()
