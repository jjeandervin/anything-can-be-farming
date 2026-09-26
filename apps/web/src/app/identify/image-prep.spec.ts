import { afterEach, describe, expect, it, vi } from 'vitest';
import { ImagePrepError, JPEG_QUALITY, MAX_EDGE, fitWithin, prepareImage } from './image-prep';

describe('fitWithin', () => {
  it.each([
    ['landscape', 4032, 3024, { width: 1600, height: 1200 }],
    ['portrait', 3024, 4032, { width: 1200, height: 1600 }],
    ['square', 3000, 3000, { width: 1600, height: 1600 }],
    ['exactly at the limit', 1600, 900, { width: 1600, height: 900 }],
    ['small (no upscale)', 800, 600, { width: 800, height: 600 }],
    ['extreme panorama', 20000, 10, { width: 1600, height: 1 }],
  ])('%s', (_, width, height, expected) => {
    expect(fitWithin(width, height, MAX_EDGE)).toEqual(expected);
  });
});

describe('prepareImage', () => {
  afterEach(() => vi.unstubAllGlobals());

  const photo = () => new File([new Uint8Array([1, 2, 3])], 'IMG_0001.HEIC', { type: 'image/heic' });

  it('throws ImagePrepError when the browser cannot decode the photo', async () => {
    vi.stubGlobal('createImageBitmap', vi.fn().mockRejectedValue(new DOMException('Unsupported', 'InvalidStateError')));
    const error = await prepareImage(photo()).catch(e => e);
    expect(error).toBeInstanceOf(ImagePrepError);
    expect(error.message).toBe("This photo format isn't supported here. Try a JPEG or PNG.");
  });

  it('applies EXIF orientation, downscales, and re-encodes as JPEG', async () => {
    const bitmap = { width: 4000, height: 3000, close: vi.fn() };
    const decode = vi.fn().mockResolvedValue(bitmap);
    const context = { fillRect: vi.fn(), drawImage: vi.fn(), fillStyle: '', imageSmoothingQuality: '' };
    const convertToBlob = vi.fn().mockResolvedValue(new Blob(['jpeg'], { type: 'image/jpeg' }));
    const sizes: number[][] = [];
    vi.stubGlobal('createImageBitmap', decode);
    vi.stubGlobal('OffscreenCanvas', class {
      constructor(width: number, height: number) { sizes.push([width, height]); }
      getContext() { return context; }
      convertToBlob = convertToBlob;
    });

    const input = photo();
    const result = await prepareImage(input);

    expect(decode).toHaveBeenCalledWith(input, { imageOrientation: 'from-image' });
    expect(sizes).toEqual([[1600, 1200]]);
    expect(context.drawImage).toHaveBeenCalledWith(bitmap, 0, 0, 1600, 1200);
    expect(convertToBlob).toHaveBeenCalledWith({ type: 'image/jpeg', quality: JPEG_QUALITY });
    expect(result.name).toBe('photo.jpg');
    expect(result.type).toBe('image/jpeg');
    expect(bitmap.close).toHaveBeenCalled();
  });

  it('rejects a canvas that cannot produce JPEG', async () => {
    vi.stubGlobal('createImageBitmap', vi.fn().mockResolvedValue({ width: 10, height: 10, close: vi.fn() }));
    vi.stubGlobal('OffscreenCanvas', class {
      getContext() { return { fillRect() {}, drawImage() {} }; }
      convertToBlob() { return Promise.resolve(new Blob(['png'], { type: 'image/png' })); }
    });
    await expect(prepareImage(photo())).rejects.toBeInstanceOf(ImagePrepError);
  });
});
