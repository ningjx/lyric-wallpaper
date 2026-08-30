import type { LyricLine } from "./parser";

/**
 * 歌词时间轴：给定播放时间，二分查找当前应显示的行索引。
 * 返回 -1 表示还没有任何歌词行开始。
 */
export function findCurrentLine(lines: LyricLine[], time: number): number {
  if (lines.length === 0) return -1;
  let lo = 0;
  let hi = lines.length - 1;
  let ans = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (lines[mid].time <= time) {
      ans = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return ans;
}
