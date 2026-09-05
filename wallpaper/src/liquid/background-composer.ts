export type BackgroundLayout = 0 | 1 | 2 | 3;

export interface BackgroundOptions {
  source: string;
  layout: BackgroundLayout;
  scale: number;
  offsetX: number;
  offsetY: number;
  pixelRatio: number;
}

/**
 * 把用户图像先排版为与渲染画布同一尺寸的纹理。这样上游玻璃渲染器仍然
 * 只需采样一张“背景纹理”，折射与背景画面不会因为布局模式而错位。
 */
export class BackgroundComposer {
  private image: HTMLImageElement | null = null;
  private imageSource = "";
  private activeObjectUrl: string | null = null;
  private readonly objectUrls = new Set<string>();

  async compose(options: BackgroundOptions, width: number, height: number): Promise<string> {
    const image = await this.load(options.source);
    const ratio = Math.max(.5, options.pixelRatio);
    const outputWidth = Math.max(1, Math.round(width * ratio));
    const outputHeight = Math.max(1, Math.round(height * ratio));
    const canvas = document.createElement("canvas");
    canvas.width = outputWidth;
    canvas.height = outputHeight;
    const ctx = canvas.getContext("2d")!;
    ctx.fillStyle = "#071117";
    ctx.fillRect(0, 0, outputWidth, outputHeight);
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";

    const scale = Math.max(.05, options.scale);
    const offsetX = options.offsetX / 100 * outputWidth;
    const offsetY = options.offsetY / 100 * outputHeight;
    const sourceWidth = Math.max(1, image.naturalWidth);
    const sourceHeight = Math.max(1, image.naturalHeight);
    const coverScale = Math.max(outputWidth / sourceWidth, outputHeight / sourceHeight) * scale;
    const containScale = Math.min(outputWidth / sourceWidth, outputHeight / sourceHeight) * scale;

    if (options.layout === 3) {
      // 平铺以画面短边为基准，初始比例便能在任意屏幕上看见重复效果。
      const tileScale = Math.min(outputWidth, outputHeight) / Math.min(sourceWidth, sourceHeight) * scale;
      const tileWidth = Math.max(1, sourceWidth * tileScale);
      const tileHeight = Math.max(1, sourceHeight * tileScale);
      const startX = ((offsetX % tileWidth) + tileWidth) % tileWidth - tileWidth;
      const startY = ((offsetY % tileHeight) + tileHeight) % tileHeight - tileHeight;
      for (let y = startY; y < outputHeight; y += tileHeight) {
        for (let x = startX; x < outputWidth; x += tileWidth) {
          ctx.drawImage(image, x, y, tileWidth, tileHeight);
        }
      }
    } else {
      const factor = options.layout === 1 ? containScale : options.layout === 2
        ? null
        : coverScale;
      const drawWidth = factor === null ? outputWidth * scale : sourceWidth * factor;
      const drawHeight = factor === null ? outputHeight * scale : sourceHeight * factor;
      ctx.drawImage(
        image,
        (outputWidth - drawWidth) / 2 + offsetX,
        (outputHeight - drawHeight) / 2 + offsetY,
        drawWidth,
        drawHeight,
      );
    }

    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, "image/png"));
    if (!blob) throw new Error("Unable to compose background texture");
    const url = URL.createObjectURL(blob);
    this.objectUrls.add(url);
    return url;
  }

  /** 在 WebGL 已上传新纹理后才释放上一张 Blob，避免快速调参时出现竞态。 */
  activate(url: string): void {
    for (const candidate of this.objectUrls) {
      if (candidate !== url) {
        URL.revokeObjectURL(candidate);
        this.objectUrls.delete(candidate);
      }
    }
    this.activeObjectUrl = url;
  }

  discard(url: string): void {
    if (url === this.activeObjectUrl) return;
    if (this.objectUrls.delete(url)) URL.revokeObjectURL(url);
  }

  dispose(): void {
    for (const url of this.objectUrls) URL.revokeObjectURL(url);
    this.objectUrls.clear();
    this.activeObjectUrl = null;
    this.image = null;
    this.imageSource = "";
  }

  private async load(source: string): Promise<HTMLImageElement> {
    if (this.image && this.imageSource === source) return this.image;
    const image = new Image();
    // Wallpaper Engine 的 file 属性会返回 file:/// 路径；它不需要 CORS。
    if (!source.startsWith("file:")) image.crossOrigin = "anonymous";
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve();
      image.onerror = () => reject(new Error(`Failed to load background: ${source}`));
      image.src = source;
    });
    this.image = image;
    this.imageSource = source;
    return image;
  }
}
