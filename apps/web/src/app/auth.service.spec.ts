import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from './auth.service';
import { RUNTIME_CONFIG } from './runtime-config';

const adapter = vi.hoisted(() => ({
  init: vi.fn(), login: vi.fn(), logout: vi.fn(), updateToken: vi.fn(), clearToken: vi.fn(),
  authenticated: false, token: 'access-token', tokenParsed: { name: 'Test User' },
  onAuthLogout: () => {}, onAuthRefreshError: () => {},
}));
vi.mock('keycloak-js', () => ({ default: class { constructor() { return adapter; } } }));

describe('Keycloak session lifecycle', () => {
  let auth: AuthService;
  beforeEach(() => {
    vi.clearAllMocks();
    adapter.authenticated = false;
    adapter.init.mockResolvedValue(false);
    adapter.updateToken.mockResolvedValue(false);
    TestBed.configureTestingModule({ providers: [
      { provide: RUNTIME_CONFIG, useValue: {
        keycloakIssuer: 'https://identity.example/realms/acbf', keycloakClientId: 'acbf-web',
      } },
    ] });
    auth = TestBed.inject(AuthService);
  });

  it('uses code flow with S256 PKCE and checks the SSO session after reload', async () => {
    adapter.authenticated = true;
    await auth.initialize();
    expect(adapter.init).toHaveBeenCalledWith(expect.objectContaining({
      onLoad: 'check-sso', flow: 'standard', pkceMethod: 'S256',
    }));
    expect(auth.authenticated()).toBe(true);
    expect(auth.name()).toBe('Test User');
    await auth.initialize();
    expect(adapter.init).toHaveBeenCalledTimes(1);
  });

  it('refreshes before returning an access token', async () => {
    adapter.authenticated = true;
    expect(await auth.accessToken()).toBe('access-token');
    expect(adapter.updateToken).toHaveBeenCalledWith(30);
  });

  it('clears authentication when refresh fails', async () => {
    adapter.authenticated = true;
    adapter.updateToken.mockRejectedValue(new Error('Expired'));
    await expect(auth.accessToken()).rejects.toThrow();
    expect(adapter.clearToken).toHaveBeenCalled();
    expect(auth.authenticated()).toBe(false);
    expect(auth.error()).toContain('expired');
  });

  it('keeps a usable signed-out state if the identity server is unavailable', async () => {
    adapter.init.mockRejectedValue(new Error('Offline'));
    await auth.initialize();
    expect(auth.ready()).toBe(true);
    expect(auth.authenticated()).toBe(false);
    expect(auth.error()).toContain('unavailable');
  });

  it('provides sign-in and sign-out redirects back to the application', async () => {
    await auth.initialize();
    await auth.signIn();
    await auth.signOut();
    expect(adapter.login).toHaveBeenCalledWith({ redirectUri: window.location.origin + '/' });
    expect(adapter.logout).toHaveBeenCalledWith({ redirectUri: window.location.origin + '/' });
  });

  it('does not leave the page waiting forever on an unresponsive SSO iframe', async () => {
    vi.useFakeTimers();
    try {
      adapter.init.mockReturnValue(new Promise(() => {}));
      const initialization = auth.initialize();
      await vi.advanceTimersByTimeAsync(15000);
      await initialization;
      expect(auth.ready()).toBe(true);
      expect(auth.error()).toContain('unavailable');
    } finally {
      vi.useRealTimers();
    }
  });
});
