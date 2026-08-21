/** 一行同步歌词 */
export interface LyricLine {
  /** 开始时间（秒） */
  time: number;
  /** 歌词文本 */
  text: string;
  /** 对应翻译（可无） */
  translated?: string;
}

const TIME_TAG_RE = /\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]/g;
/** 元信息行，如 [by:xxx] [ar:xxx] [ti:xxx]，跳过 */
const META_TAG_RE = /^\[[a-zA-Z]+:/;

/**
 * 行尾标点（句读/语气类）：逗号、句号、顿号、分号、冒号、感叹号、问号、省略号等。
 * 歌词是分行显示的，行尾这些标点没有意义，统一去掉；
 * 但保留括号、引号等"内容性"标点（如（前奏）、"夜空中最亮的星"）。
 */
const TRAILING_PUNCT_RE = /[，。、；：！？．….,;:!?]+$/;

/** 去掉行尾标点（连续多个也一并去掉） */
function stripTrailingPunctuation(text: string): string {
  return text.replace(TRAILING_PUNCT_RE, "");
}

/**
 * 解析 LRC 文本为歌词行数组（按时间升序）。
 * - 跳过网易云 JSON 元数据行（形如 {"t":0,"c":[...]}，作词/作曲信息）
 * - 跳过 [by:] 等元信息行
 * - 支持一行多个时间标签
 * - 过滤空文本行
 */
export function parseLrc(lrc: string): LyricLine[] {
  const lines: LyricLine[] = [];
  if (!lrc) return lines;

  for (const raw of lrc.split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("{") || META_TAG_RE.test(line)) continue;

    // 收集本行所有时间标签
    TIME_TAG_RE.lastIndex = 0;
    const times: number[] = [];
    let m: RegExpExecArray | null;
    while ((m = TIME_TAG_RE.exec(line)) !== null) {
      const min = Number(m[1]);
      const sec = Number(m[2]);
      const frac = m[3] ? Number(m[3].padEnd(3, "0").slice(0, 3)) : 0;
      times.push(min * 60 + sec + frac / 1000);
    }
    if (times.length === 0) continue;

    // 去掉行尾标点；若整行只剩标点则跳过
    const text = stripTrailingPunctuation(line.replace(TIME_TAG_RE, "").trim());
    if (!text) continue;
    for (const t of times) {
      lines.push({ time: t, text });
    }
  }

  lines.sort((a, b) => a.time - b.time);
  return lines;
}

/**
 * 把翻译歌词按时间合并进原歌词。
 * 每条原歌词取"时间不早于它且最接近"的翻译行，时间差超过 8 秒则忽略。
 */
export function mergeTranslation(original: LyricLine[], translated: LyricLine[]): LyricLine[] {
  const merged: LyricLine[] = original.map((l) => ({ ...l }));
  if (translated.length === 0) return merged;

  let ti = 0;
  for (const line of merged) {
    while (ti < translated.length - 1 && translated[ti + 1].time <= line.time + 0.01) {
      ti++;
    }
    const t = translated[ti];
    if (t && line.time - t.time < 8) {
      line.translated = t.text;
    }
  }
  return merged;
}
