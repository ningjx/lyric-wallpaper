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

  /**
   * 合成后的 Canvas 直接作为 WebGL 的 texImage2D 输入。旧实现会把它编码为
   * PNG Blob，再由 Image 重新解码后上传；这会在切换大背景时制造额外的主
   * 线程工作、内存峰值和一次无意义的编解码。
   */
  async compose(options: BackgroundOptions, width: number, height: number): Promise<HTMLCanvasElement> {
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

    return canvas;
  }

  dispose(): void {
    this.image = null;
    this.imageSource = "";
  }

  uploadCanvas(renderer: LiquidGlassRenderer, canvas: HTMLCanvasElement): void {
    this.uploadTexture(renderer, canvas, canvas.width, canvas.height);
  }

  /**
   * 本地 file:/// 图像不能被某些 Wallpaper Engine Chromium 版本写回 Canvas，
   * 但可直接作为 WebGL 纹理上传。布局合成失败时用此路径保证用户的图仍可见。
   */
  async uploadSource(renderer: LiquidGlassRenderer, source: string): Promise<void> {
    const image = await this.load(source);
    this.uploadTexture(renderer, image, image.naturalWidth || 1, image.naturalHeight || 1);
  }

  private uploadTexture(renderer: LiquidGlassRenderer, source: TexImageSource, width: number, height: number): void {
    const gl = renderer.gl;
    const texture = gl.createTexture();
    if (!texture) throw new Error("Unable to create background texture");
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    const isPowerOfTwo = (width & (width - 1)) === 0 && (height & (height - 1)) === 0;
    if (isPowerOfTwo) {
      gl.generateMipmap(gl.TEXTURE_2D);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    } else {
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    }
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    if (renderer.wallpaperTexture) gl.deleteTexture(renderer.wallpaperTexture);
    renderer.wallpaperTexture = texture;
    renderer.wallpaperSize = [width, height];
    renderer.wallpaperReady = true;
    renderer.clearBackdropBlurCache();
    renderer.wallpaperVersion++;
    renderer.markAllDirty();
    renderer.requestRender();
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
import type { LiquidGlassRenderer } from "../../vendor/liquid-glass-webgl/src/components/liquid-glass/renderer";
