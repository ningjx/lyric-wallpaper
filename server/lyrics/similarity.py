# -*- coding: utf-8 -*-
"""歌曲信息相似度算法（0-100）。

移植自 Widdit/now-playing-service 的 SongMatchingUtil（Apache-2.0），
用于在搜索结果的多个候选中挑出最佳匹配、以及多歌词源之间判优选优。

核心思路：
  - 归一化：小写、统括号、全角转半角、压空格（简繁转换只做最常见字，见 _zh 表）；
  - 歌名分（权重 65%）：主标题严格匹配 + 括号内「版本关键词」惩罚；
  - 歌手分（权重 35%）：多歌手列表按命中比例加权；
  - 歌手完全不匹配时大幅降分，宁可返回空也不要错词。
"""
from __future__ import annotations

import re
from typing import List, Optional, Pattern, Set

# 精确匹配阈值（≥ 此分视为匹配成功，默认）
EXACT_MATCH_THRESHOLD = 85
# 及格线阈值（同一首歌不同版本）
ALTERNATE_VERSION_THRESHOLD = 60

# 改变歌曲性质的关键词（严格版本匹配）
_SIGNIFICANT_KEYWORDS: Set[str] = {
    "remix", "live", "acoustic", "instrumental", "cover",
    "edit", "mix", "dj", "radio", "extended", "remaster", "remastered",
    "unplugged", "demo", "bootleg", "mashup", "orchestral", "symphony",
    "stripped", "sped up", "slowed", "reverb", "0.8x", "1.1x", "1.2x",
    "现场", "翻唱", "伴奏", "钢琴版", "吉他版", "改编", "翻自",
    "重制", "混音", "慢速", "快速", "加速", "倍速", "粤语版", "填词",
    "dj版",
}

_SIGNIFICANT_PATTERNS: List[Pattern] = [
    re.compile(r"\bver\.?\b", re.IGNORECASE),
    re.compile(r"\bversion\b", re.IGNORECASE),
    re.compile(r"\bedition\b", re.IGNORECASE),
    re.compile(r"\bremix\b", re.IGNORECASE),
    re.compile(r"\blive\b", re.IGNORECASE),
    re.compile(r"\bacoustic\b", re.IGNORECASE),
    re.compile(r"\binstrumental\b", re.IGNORECASE),
    re.compile(r"\bcover\b", re.IGNORECASE),
    re.compile(r"\bdemo\b", re.IGNORECASE),
    re.compile(r"\bremaster(ed)?\b", re.IGNORECASE),
    re.compile(r"\bunplugged\b", re.IGNORECASE),
    re.compile(r"\bkaraoke\b", re.IGNORECASE),
    re.compile(r"\bextended\b", re.IGNORECASE),
    re.compile(r"\bradio\b", re.IGNORECASE),
]

# 表示翻译/别名的无害关键词
_HARMLESS: Set[str] = {
    "explicit", "clean", "original mix", "feat", "ft.", "翻译", "译名",
    "又名", "别名", "原名", "aka",
}

# 常见繁体→简体（少量高频字，够覆盖绝大多数歌名；全量表见 OpenCC）
_TRAD_TO_SIMP = str.maketrans({
    "國": "国", "語": "语", "體": "体", "樂": "乐", "後": "后",
    "來": "来", "時": "时", "間": "间", "個": "个", "們": "们",
    "無": "无", "為": "为", "與": "与", "愛": "爱", "會": "会",
    "對": "对", "邊": "边", "這": "这", "說": "说", "聽": "听",
    "萬": "万", "點": "点", "實": "实", "麼": "么", "還": "还",
    "讓": "让", "開": "开", "關": "关", "學": "学", "習": "习",
    "動": "动", "沒": "没", "問": "问", "幾": "几", "樣": "样",
    "種": "种", "總": "总", "寶": "宝", "東": "东", "貝": "贝",
    "見": "见", "門": "门", "風": "风", "飛": "飞", "馬": "马",
    "聲": "声", "覺": "觉", "觀": "观", "進": "进", "過": "过",
    "選": "选", "員": "员", "車": "车", "轉": "转", "龍": "龙",
    "魚": "鱼", "鳥": "鸟",
})


def _zh_to_simplified(text: str) -> str:
    return text.translate(_TRAD_TO_SIMP)


def _unify_brackets(text: str) -> str:
    mapping = {
        "（": "(", "）": ")", "[": "(", "]": ")",
        "【": "(", "】": ")", "「": "(", "」": ")",
        "『": "(", "』": ")", "〔": "(", "〕": ")",
        "〈": "(", "〉": ")",
    }
    return "".join(mapping.get(c, c) for c in text)


def _fullwidth_to_halfwidth(text: str) -> str:
    out = []
    for c in text:
        code = ord(c)
        if 0xFF01 <= code <= 0xFF5E:
            out.append(chr(code - 0xFEE0))
        elif c == "\u3000":
            out.append(" ")
        else:
            out.append(c)
    return "".join(out)


def normalize(text: Optional[str]) -> str:
    if not text:
        return ""
    text = _zh_to_simplified(text)
    text = text.lower()
    text = _unify_brackets(text)
    text = _fullwidth_to_halfwidth(text)
    text = re.sub(r"\s+", " ", text).strip()
    return text


# --------------------------------------------------------------------------
# 字符串相似度
# --------------------------------------------------------------------------
def _levenshtein(s1: str, s2: str) -> int:
    if not s1:
        return len(s2)
    if not s2:
        return len(s1)
    prev = list(range(len(s2) + 1))
    for i, c1 in enumerate(s1, 1):
        curr = [i]
        for j, c2 in enumerate(s2, 1):
            cost = 0 if c1 == c2 else 1
            curr.append(min(prev[j] + 1, curr[j - 1] + 1, prev[j - 1] + cost))
        prev = curr
    return prev[-1]


def string_similarity(s1: Optional[str], s2: Optional[str]) -> int:
    if not s1 or not s2:
        return 0
    s1, s2 = s1.strip().lower(), s2.strip().lower()
    if s1 == s2:
        return 100
    if not s1 or not s2:
        return 0
    if s2 in s1:
        ratio = len(s2) * 100 // len(s1)
        return 70 + ratio * 30 // 100
    if s1 in s2:
        ratio = len(s1) * 100 // len(s2)
        return 70 + ratio * 30 // 100
    dist = _levenshtein(s1, s2)
    max_len = max(len(s1), len(s2))
    return max(0, 100 - dist * 100 // max_len)


# --------------------------------------------------------------------------
# 标题 / 歌手评分
# --------------------------------------------------------------------------
def _extract_base_title(title: str) -> str:
    return re.sub(r"\s*\([^)]*\)\s*", " ", title).strip()


def _extract_extras(title: str) -> List[str]:
    return [m.group(1).strip().lower()
            for m in re.finditer(r"\(([^)]+)\)", title) if m.group(1).strip()]


def _contains_significant(text: str) -> bool:
    low = text.lower()
    if any(k in low for k in _SIGNIFICANT_KEYWORDS):
        return True
    return any(p.search(low) for p in _SIGNIFICANT_PATTERNS)


def _is_harmless(text: str) -> bool:
    return any(k in text.lower() for k in _HARMLESS)


def _title_score(local: str, cloud: str) -> int:
    if local == cloud:
        return 100
    local_base, cloud_base = _extract_base_title(local), _extract_base_title(cloud)
    base_sim = string_similarity(local_base, cloud_base)
    if base_sim < 95:
        if base_sim < 70:
            return base_sim // 4
        if base_sim < 85:
            return base_sim // 3
        return base_sim // 2

    score = 100
    if base_sim < 100:
        score -= (100 - base_sim)

    local_extras = _extract_extras(local)
    cloud_extras = _extract_extras(cloud)
    score -= _extra_penalty(local_extras, cloud_extras)
    return max(0, score)


def _extra_penalty(local: List[str], cloud: List[str]) -> int:
    if not local and not cloud:
        return 0
    if local and not cloud:
        sig = any(_contains_significant(e) for e in local)
        if sig:
            return 55
        if all(_is_harmless(e) for e in local):
            return 5
        return 25
    if not local and cloud:
        sig = any(_contains_significant(e) for e in cloud)
        if sig:
            return 50
        alias = sum(1 for e in cloud if _is_harmless(e))
        if alias == len(cloud):
            return 2 * alias
        return 15
    # 两者都有：比较重要关键词交集，简化实现
    loc_sig = {k for e in local if _contains_significant(e) for k in
               ([k for k in _SIGNIFICANT_KEYWORDS if k in e.lower()] or ["?"])}
    cloud_sig = {k for e in cloud if _contains_significant(e) for k in
                 ([k for k in _SIGNIFICANT_KEYWORDS if k in e.lower()] or ["?"])}
    if loc_sig and not cloud_sig:
        return 50
    if cloud_sig and not loc_sig:
        return 45
    if loc_sig and cloud_sig:
        inter = loc_sig & cloud_sig
        if not inter:
            return 55
        if len(loc_sig - inter) == 0 and len(cloud_sig - inter) == 0:
            # 关键词完全一致，比较细节
            return _detailed_extra_penalty(local, cloud)
        ratio = len(inter) / max(len(loc_sig), len(cloud_sig))
        return int((1 - ratio) * 40)
    return _detailed_extra_penalty(local, cloud)


def _detailed_extra_penalty(local: List[str], cloud: List[str]) -> int:
    total, count = 0, 0
    for e in local:
        best = max((string_similarity(e, c) for c in cloud), default=0)
        total += best
        count += 1
    if count == 0:
        return 0
    avg = total // count
    if avg >= 90:
        return 5
    if avg >= 70:
        return 15
    if avg >= 50:
        return 30
    return 45


def _parse_artists(artist: str) -> List[str]:
    parts = [p.strip() for p in artist.split("/") if p.strip()]
    return [_extract_base_title(p) for p in parts if _extract_base_title(p)]


def _artist_score(local: str, cloud: str) -> int:
    locals_ = _parse_artists(local)
    clouds = _parse_artists(cloud)
    if not locals_ and not clouds:
        return 100
    if not locals_ or not clouds:
        return 50

    def count(src: List[str], tgt: List[str]) -> int:
        n = 0
        for s in src:
            for t in tgt:
                if string_similarity(s, t) >= 80:
                    n += 1
                    break
        return n

    local_match = count(locals_, clouds)
    if local_match == 0:
        return 0
    if len(locals_) == 1 and local_match == 1:
        return 100
    cloud_match = count(clouds, locals_)
    local_ratio = local_match / len(locals_)
    cloud_ratio = cloud_match / len(clouds)
    score = int(local_ratio * 70 + cloud_ratio * 30)
    return max(score, 55)


def calculate_similarity(local_title: Optional[str], local_artist: Optional[str],
                         cloud_title: Optional[str],
                         cloud_artist: Optional[str]) -> int:
    """0-100；本地（播放器读到的） vs 云端（搜索结果）的匹配分。"""
    if not local_title or not cloud_title:
        return 0
    local_title = normalize(local_title)
    local_artist = normalize(local_artist)
    cloud_title = normalize(cloud_title)
    cloud_artist = normalize(cloud_artist)

    title_score = _title_score(local_title, cloud_title)
    if title_score < 30:
        return title_score

    artist_score = _artist_score(local_artist, cloud_artist)
    if artist_score == 0 and local_artist and cloud_artist:
        return min(title_score // 2, 40)

    total = int(title_score * 0.65 + artist_score * 0.35)
    return max(0, min(100, total))