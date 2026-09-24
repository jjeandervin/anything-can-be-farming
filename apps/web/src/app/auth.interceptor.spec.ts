import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { AuthService } from './auth.service';
import { authInterceptor } from './auth.interceptor';
import { RUNTIME_CONFIG } from './runtime-config';

describe('API token attachment', () => {
  let http: HttpClient;
  let requests: HttpTestingController;
  let accessToken: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    accessToken = vi.fn().mockResolvedValue('test-access-token');
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(),
      { provide: AuthService, useValue: { accessToken } },
      { provide: RUNTIME_CONFIG, useValue: { apiBaseUrl: 'https://localhost:7243' } },
    ] });
    http = TestBed.inject(HttpClient);
    requests = TestBed.inject(HttpTestingController);
  });
  afterEach(() => requests.verify());

  it('attaches a refreshed access token to the protected API', async () => {
    const response = firstValueFrom(http.get('https://localhost:7243/api/auth/me'));
    await Promise.resolve();
    const request = requests.expectOne('https://localhost:7243/api/auth/me');
    expect(request.request.headers.get('Authorization')).toBe('Bearer test-access-token');
    request.flush({ authenticated: true });
    await response;
  });

  it.each([
    'https://localhost.evil.example:7243/api/auth/me',
    'https://localhost:7243@evil.example/api/auth/me',
    'https://localhost:7244/api/auth/me',
    'https://localhost:7243/unrelated',
    'https://localhost:7243/api/status',
    'https://localhost:7243/api/status/database',
  ])('does not send credentials or wait for authentication at %s', async url => {
    const response = firstValueFrom(http.get(url));
    const request = requests.expectOne(url);
    expect(request.request.headers.has('Authorization')).toBe(false);
    expect(accessToken).not.toHaveBeenCalled();
    request.flush({});
    await response;
  });

  it('allows an anonymous request when signed out', async () => {
    accessToken.mockResolvedValue(undefined);
    const response = firstValueFrom(http.get('https://localhost:7243/api/auth/me'));
    await Promise.resolve();
    const request = requests.expectOne('https://localhost:7243/api/auth/me');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({});
    await response;
  });

  it('does not send a stale token if renewal fails', async () => {
    accessToken.mockRejectedValue(new Error('Session expired'));
    await expect(firstValueFrom(http.get('https://localhost:7243/api/auth/me')))
      .rejects.toThrow('Session expired');
    requests.expectNone('https://localhost:7243/api/auth/me');
  });
});
