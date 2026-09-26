import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from '../auth.service';
import { RUNTIME_CONFIG } from '../runtime-config';
import { StatusPage } from './status-page';

describe('StatusPage', () => {
  const api = 'https://localhost:7243';
  const auth = {
    ready: signal(true), authenticated: signal(false), name: signal('Test User'), error: signal(''),
    initialize: vi.fn().mockResolvedValue(undefined), signIn: vi.fn(), signOut: vi.fn(),
  };
  let requests: HttpTestingController;

  beforeEach(() => {
    vi.clearAllMocks();
    auth.authenticated.set(false);
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthService, useValue: auth },
      { provide: RUNTIME_CONFIG, useValue: { apiBaseUrl: api } },
    ] });
    requests = TestBed.inject(HttpTestingController);
  });
  afterEach(() => requests.verify());

  function create() {
    const fixture = TestBed.createComponent(StatusPage);
    fixture.detectChanges();
    return fixture;
  }

  async function settle(fixture: ReturnType<typeof create>) {
    for (let i = 0; i < 5; i++) await Promise.resolve();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function rows(fixture: ReturnType<typeof create>) {
    const element = fixture.nativeElement as HTMLElement;
    return Object.fromEntries([...element.querySelectorAll('dl > div')].map(row =>
      [row.querySelector('dt')!.textContent!.trim(), row.querySelector('dd')!.textContent!.trim()]));
  }

  it('reports each connection check and the signed-out identity state', async () => {
    const fixture = create();
    requests.expectOne(api + '/api/status').flush({ status: 'ok' });
    requests.expectOne(api + '/api/status/database')
      .flush({ database: 'unavailable' }, { status: 503, statusText: 'Service Unavailable' });
    await settle(fixture);
    expect(rows(fixture)).toEqual({
      API: 'Connected', Database: 'Unavailable', Authentication: 'Not signed in',
      'Protected API': 'Sign in to verify',
    });
    expect((fixture.nativeElement as HTMLElement).querySelector('button')?.textContent?.trim()).toBe('Sign in');
  });

  it('verifies the protected API when signed in', async () => {
    auth.authenticated.set(true);
    const fixture = create();
    requests.expectOne(api + '/api/status').flush({ status: 'ok' });
    requests.expectOne(api + '/api/status/database').flush({ database: 'connected' });
    await settle(fixture);
    for (const request of requests.match(api + '/api/auth/me'))
      request.flush({ authenticated: true, subject: 's', username: 'tester', displayName: 'Test User' });
    await settle(fixture);
    expect(rows(fixture)).toEqual({
      API: 'Connected', Database: 'Connected', Authentication: 'Signed in as Test User',
      'Protected API': 'Verified as Test User',
    });
  });

  it('shows the API hint when the API is unreachable', async () => {
    const fixture = create();
    requests.expectOne(api + '/api/status').error(new ProgressEvent('error'));
    requests.expectOne(api + '/api/status/database').error(new ProgressEvent('error'));
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('.hint')?.textContent)
      .toContain('Check that the API is running');
  });
});
