import { DEFAULT_LIQUID_SETTINGS, type LiquidSettings, type ReferenceLyricsWallpaper } from "./reference-wallpaper";

type Control = {
  key: keyof LiquidSettings;
  label: string;
  kind: "range" | "checkbox" | "color" | "select";
  min?: number;
  max?: number;
  step?: number;
  options?: Array<[string, string]>;
};

const groups: Array<{ title: string; controls: Control[] }> = [
  { title: "歌词排版", controls: [
    { key: "lyricFontScale", label: "字体大小", kind: "range", min: .5, max: 1.6, step: .01 },
    { key: "lyricGap", label: "歌词间距", kind: "range", min: 0, max: 120, step: 1 },
    { key: "lyricVerticalOffset", label: "垂直微调", kind: "range", min: -40, max: 40, step: 1 },
    { key: "lyricScrollSpeed", label: "滚动速度", kind: "range", min: 2, max: 20, step: .1 },
    { key: "lyricOffsetX", label: "整体水平", kind: "range", min: -1000, max: 1000, step: 1 },
    { key: "lyricOffsetY", label: "整体垂直", kind: "range", min: -600, max: 600, step: 1 },
    { key: "lyricAlignment", label: "屏幕对齐", kind: "select", options: [["0", "居中"], ["1", "靠左"], ["2", "靠右"]] },
  ] },
  { title: "空间层次", controls: [
    { key: "lyricDepthMinScale", label: "远处最小缩放", kind: "range", min: .35, max: .95, step: .01 },
    { key: "lyricDepthScaleFalloff", label: "缩放衰减", kind: "range", min: .05, max: 2, step: .01 },
    { key: "lyricDepthScaleCurve", label: "缩放曲线", kind: "range", min: .5, max: 4, step: .01 },
    { key: "lyricDepthAlphaFalloff", label: "淡出衰减", kind: "range", min: .05, max: 2, step: .01 },
    { key: "lyricDepthAlphaCurve", label: "淡出曲线", kind: "range", min: .5, max: 4, step: .01 },
    { key: "lyricDepthGlassFloor", label: "远处玻璃保留", kind: "range", min: 0, max: 1, step: .01 },
    { key: "lyricDepthCullDistance", label: "渲染距离", kind: "range", min: 1, max: 6, step: .1 },
  ] },
  { title: "光学", controls: [
    { key: "refractionHeight", label: "折射高度", kind: "range", min: 0, max: 56, step: 1 },
    { key: "refractionAmount", label: "折射强度", kind: "range", min: -80, max: 20, step: 1 },
    { key: "blurRadius", label: "背景模糊", kind: "range", min: 0, max: 36, step: 1 },
    { key: "saturation", label: "饱和度", kind: "range", min: 0, max: 2.5, step: .01 },
    { key: "brightness", label: "亮度", kind: "range", min: -.5, max: .5, step: .01 },
    { key: "contrast", label: "对比度", kind: "range", min: .3, max: 2, step: .01 },
    { key: "cornerRadius", label: "圆角", kind: "range", min: 0, max: 180, step: 1 },
    { key: "depthEffect", label: "景深层", kind: "checkbox" },
    { key: "chromaticAberration", label: "RGB 色差", kind: "checkbox" },
  ] },
  { title: "材质", controls: [
    { key: "tintColor", label: "染色", kind: "color" },
    { key: "tintAlpha", label: "染色不透明度", kind: "range", min: 0, max: .6, step: .005 },
    { key: "surfaceColor", label: "表面色", kind: "color" },
    { key: "surfaceAlpha", label: "表面不透明度", kind: "range", min: 0, max: .6, step: .005 },
  ] },
  { title: "高光", controls: [
    { key: "highlight", label: "启用高光", kind: "checkbox" },
    { key: "highlightMode", label: "模式", kind: "select", options: [["0", "默认"], ["1", "环境"], ["2", "纯净"]] },
    { key: "highlightColor", label: "高光颜色", kind: "color" },
    { key: "highlightAlpha", label: "强度", kind: "range", min: 0, max: 1, step: .01 },
    { key: "highlightAngle", label: "光线角度", kind: "range", min: -3.14, max: 3.14, step: .01 },
    { key: "highlightFalloff", label: "衰减", kind: "range", min: .2, max: 5, step: .01 },
    { key: "highlightWidth", label: "边缘宽度", kind: "range", min: .2, max: 6, step: .1 },
  ] },
  { title: "阴影", controls: [
    { key: "shadow", label: "启用阴影", kind: "checkbox" },
    { key: "shadowColor", label: "阴影颜色", kind: "color" },
    { key: "shadowAlpha", label: "不透明度", kind: "range", min: 0, max: 1, step: .01 },
    { key: "shadowRadius", label: "模糊半径", kind: "range", min: 0, max: 80, step: 1 },
    { key: "shadowOffsetX", label: "水平偏移", kind: "range", min: -50, max: 50, step: 1 },
    { key: "shadowOffsetY", label: "垂直偏移", kind: "range", min: -50, max: 80, step: 1 },
  ] },
  { title: "渲染器", controls: [
    { key: "separableBlur", label: "可分离模糊", kind: "checkbox" },
    { key: "continuousCorners", label: "连续曲率圆角", kind: "checkbox" },
    { key: "directBackdrop", label: "直接采样背景", kind: "checkbox" },
    { key: "dpr", label: "渲染像素比", kind: "range", min: .75, max: 2, step: .05 },
    { key: "blurTapCap", label: "模糊采样数", kind: "range", min: 3, max: 33, step: 2 },
    { key: "blurDownsample", label: "模糊降采样", kind: "range", min: 1, max: 4, step: 1 },
    { key: "kawaseBlur", label: "Kawase 模糊", kind: "checkbox" },
    { key: "blurCache", label: "模糊缓存", kind: "checkbox" },
    { key: "perElementFbo", label: "局部 FBO", kind: "checkbox" },
  ] },
];

export function mountLiquidControls(root: HTMLElement, wallpaper: ReferenceLyricsWallpaper): void {
  root.innerHTML = `
    <button class="liquid-controls-toggle" type="button" aria-expanded="false">液态玻璃参数 <span>⌃</span></button>
    <div class="liquid-controls-panel" hidden>
      <div class="liquid-controls-title"><strong>Liquid Glass</strong><button type="button" data-reset>恢复默认</button></div>
      <div class="liquid-controls-groups"></div>
    </div>`;
  const panel = root.querySelector<HTMLElement>(".liquid-controls-panel")!;
  const toggle = root.querySelector<HTMLButtonElement>(".liquid-controls-toggle")!;
  const content = root.querySelector<HTMLElement>(".liquid-controls-groups")!;
  const settings = wallpaper.getSettings();
  const sync = (next: LiquidSettings): void => {
    for (const control of groups.flatMap((group) => group.controls)) {
      const input = content.querySelector<HTMLInputElement | HTMLSelectElement>(`[data-setting="${control.key}"]`)!;
      const value = next[control.key];
      if (control.kind === "checkbox") (input as HTMLInputElement).checked = Boolean(value);
      else if (control.kind === "color") input.value = rgbToHex(value as [number, number, number]);
      else input.value = String(value);
      const output = input.closest<HTMLElement>("label")?.querySelector<HTMLOutputElement>("output");
      if (output) output.value = format(value);
    }
  };
  for (const group of groups) {
    const section = document.createElement("section");
    section.className = "liquid-controls-group";
    section.innerHTML = `<h2>${group.title}</h2>`;
    for (const control of group.controls) section.append(controlElement(control, settings));
    content.append(section);
  }
  sync(settings);
  toggle.addEventListener("click", () => {
    const open = panel.hidden;
    panel.hidden = !open;
    toggle.setAttribute("aria-expanded", String(open));
    root.classList.toggle("is-open", open);
  });
  const updateSetting = (event: Event): void => {
    const input = event.target as HTMLInputElement | HTMLSelectElement;
    const key = input.dataset.setting as keyof LiquidSettings | undefined;
    if (!key) return;
    const value = input.type === "checkbox" ? (input as HTMLInputElement).checked : input.type === "color" ? hexToRgb(input.value) : Number(input.value);
    wallpaper.setSettings({ [key]: value } as Partial<LiquidSettings>);
    const output = input.closest<HTMLElement>("label")?.querySelector<HTMLOutputElement>("output");
    if (output) output.value = format(value);
  };
  content.addEventListener("input", updateSetting);
  content.addEventListener("change", updateSetting);
  root.querySelector<HTMLButtonElement>("[data-reset]")!.addEventListener("click", () => {
    wallpaper.setSettings(DEFAULT_LIQUID_SETTINGS);
    sync(wallpaper.getSettings());
  });
}

function controlElement(control: Control, settings: LiquidSettings): HTMLLabelElement {
  const label = document.createElement("label");
  label.className = `liquid-control liquid-control-${control.kind}`;
  const current = settings[control.key];
  if (control.kind === "checkbox") {
    label.innerHTML = `<span>${control.label}</span><input data-setting="${control.key}" type="checkbox">`;
  } else if (control.kind === "color") {
    label.innerHTML = `<span>${control.label}</span><input data-setting="${control.key}" type="color">`;
  } else if (control.kind === "select") {
    label.innerHTML = `<span>${control.label}</span><select data-setting="${control.key}">${control.options!.map(([value, name]) => `<option value="${value}">${name}</option>`).join("")}</select>`;
  } else {
    label.innerHTML = `<span>${control.label}</span><input data-setting="${control.key}" type="range" min="${control.min}" max="${control.max}" step="${control.step}"><output>${format(current)}</output>`;
  }
  return label;
}

function rgbToHex([r, g, b]: [number, number, number]): string { return `#${[r, g, b].map((v) => Math.round(v * 255).toString(16).padStart(2, "0")).join("")}`; }
function hexToRgb(value: string): [number, number, number] { return [1, 3, 5].map((offset) => Number.parseInt(value.slice(offset, offset + 2), 16) / 255) as [number, number, number]; }
function format(value: unknown): string { return typeof value === "number" ? (Math.abs(value) < 10 ? value.toFixed(2).replace(/0+$/, "").replace(/\.$/, "") : String(Math.round(value))) : String(value); }
