import { InjectionToken } from '@angular/core';

export const MAX_EDGE = 1600;
export const JPEG_QUALITY = 0.85;
const UNSUPPORTED = "This photo format isn't supported here. Try a JPEG or PNG.";

export class ImagePrepError extends Error {
  override readonly name = 'ImagePrepError';
}

/** Scales width × height so the long edge is at most `max`, preserving aspect ratio. Never upscales. */
export function fitWithin(width: number, height: number, max: number): { width: number; height: number } {
  const scale = Math.min(1, max / Math.max(width, height));
  return {
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale)),
  };
}

/**
 * Decodes a photo (applying EXIF rotation), downscales it, and re-encodes it as JPEG.
 * Re-encoding always runs, so EXIF metadata such as GPS coordinates is never uploaded
 * and PNG or HEIC input always leaves as JPEG.
 */
export async function prepareImage(file: File): Promise<File> {
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    throw new ImagePrepError(UNSUPPORTED);
  }
  try {
    const { width, height } = fitWithin(bitmap.width, bitmap.height, MAX_EDGE);
    const blob = await encodeJpeg(bitmap, width, height);
    return new File([blob], 'photo.jpg', { type: 'image/jpeg' });
  } finally {
    bitmap.close();
  }
}

async function encodeJpeg(bitmap: ImageBitmap, width: number, height: number): Promise<Blob> {
  if (typeof OffscreenCanvas !== 'undefined') {
    const canvas = new OffscreenCanvas(width, height);
    const context = canvas.getContext('2d');
    if (context) {
      draw(context, bitmap, width, height);
      return checkJpeg(await canvas.convertToBlob({ type: 'image/jpeg', quality: JPEG_QUALITY }));
    }
  }
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d');
  if (!context) throw new ImagePrepError(UNSUPPORTED);
  draw(context, bitmap, width, height);
  const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', JPEG_QUALITY));
  return checkJpeg(blob);
}

function draw(
  context: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D,
  bitmap: ImageBitmap, width: number, height: number,
): void {
  // JPEG has no transparency; without a background, transparent PNG pixels turn black.
  context.fillStyle = '#fff';
  context.fillRect(0, 0, width, height);
  context.imageSmoothingQuality = 'high';
  context.drawImage(bitmap, 0, 0, width, height);
}

function checkJpeg(blob: Blob | null): Blob {
  // Browsers fall back to PNG for unsupported types; never send anything but JPEG.
  if (!blob || blob.type !== 'image/jpeg') throw new ImagePrepError(UNSUPPORTED);
  return blob;
}

/** Injectable seam so the page can be tested without real image decoding. */
export const IMAGE_PREPARER = new InjectionToken<(file: File) => Promise<File>>('image preparer', {
  providedIn: 'root',
  factory: () => prepareImage,
});
