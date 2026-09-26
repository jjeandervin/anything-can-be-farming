import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RUNTIME_CONFIG } from '../runtime-config';
import { IDENTIFY_TIMEOUT_MS, IdentifyService } from './identify.service';

describe('IdentifyService', () => {
  const url = 'https://localhost:7243/api/identify';
  let service: IdentifyService;
  let requests: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: RUNTIME_CONFIG, useValue: { apiBaseUrl: 'https://localhost:7243' } },
    ] });
    service = TestBed.inject(IdentifyService);
    requests = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    requests.verify();
    vi.useRealTimers();
  });

  const jpeg = (name: string) => new File([name], name, { type: 'image/jpeg' });

  it('posts images and organs as multipart form data in matching order', async () => {
    const photos = [
      { file: jpeg('a.jpg'), organ: 'flower' as const },
      { file: jpeg('b.jpg'), organ: 'auto' as const },
      { file: jpeg('c.jpg'), organ: 'bark' as const },
    ];
    const response = firstValueFrom(service.identify(photos));
    const request = requests.expectOne(url);

    expect(request.request.method).toBe('POST');
    expect(request.request.headers.has('Content-Type')).toBe(false);
    const body = request.request.body as FormData;
    expect(body).toBeInstanceOf(FormData);
    const images = body.getAll('images') as File[];
    const organs = body.getAll('organs');
    expect(images.length).toBe(organs.length);
    expect(await Promise.all(images.map(image => image.text()))).toEqual(['a.jpg', 'b.jpg', 'c.jpg']);
    expect(organs).toEqual(['flower', 'auto', 'bark']);

    const result = { bestMatch: null, remainingRequests: 3, predictedOrgans: [], results: [] };
    request.flush(result);
    expect(await response).toEqual(result);
  });

  it(`gives up after ${IDENTIFY_TIMEOUT_MS / 1000} seconds`, async () => {
    vi.useFakeTimers();
    const response = firstValueFrom(service.identify([{ file: jpeg('a.jpg'), organ: 'leaf' }]));
    const request = requests.expectOne(url);
    vi.advanceTimersByTime(IDENTIFY_TIMEOUT_MS);
    await expect(response).rejects.toMatchObject({ name: 'TimeoutError' });
    expect(request.cancelled).toBe(true);
  });
});
