import { Injectable, inject, signal } from '@angular/core';
import Keycloak from 'keycloak-js';
import { RUNTIME_CONFIG } from './runtime-config';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly config = inject(RUNTIME_CONFIG);
  private readonly issuer = new URL(this.config.keycloakIssuer.replace(/\/$/, ''));
  private readonly realmSeparator = this.issuer.pathname.lastIndexOf('/realms/');
  private readonly client = new Keycloak({
    url: this.issuer.origin + this.issuer.pathname.slice(0, this.realmSeparator),
    realm: decodeURIComponent(this.issuer.pathname.slice(this.realmSeparator + 8)),
    clientId: this.config.keycloakClientId,
  });
  readonly ready = signal(false);
  readonly authenticated = signal(false);
  readonly name = signal('');
  readonly error = signal('');
  private initialization?: Promise<void>;

  initialize(): Promise<void> {
    return this.initialization ??= this.initializeClient();
  }

  private async initializeClient(): Promise<void> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    this.client.onAuthLogout = () => this.authenticated.set(false);
    this.client.onAuthRefreshError = () => {
      this.client.clearToken();
      this.authenticated.set(false);
      this.error.set('Your session expired. Please sign in again.');
    };
    try {
      await Promise.race([this.client.init({
        onLoad: 'check-sso',
        flow: 'standard',
        pkceMethod: 'S256',
        checkLoginIframe: false,
        silentCheckSsoRedirectUri: window.location.origin + '/silent-check-sso.html',
        // A normal SSO redirect restores the session when third-party cookies are blocked.
        silentCheckSsoFallback: true,
        redirectUri: window.location.origin + '/',
        scope: 'openid profile',
      }), new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error('Authentication check timed out.')), 15000);
      })]);
      this.updateState();
    } catch {
      this.error.set('Authentication is unavailable. Check the Keycloak client configuration.');
    } finally {
      clearTimeout(timer);
      this.ready.set(true);
    }
  }

  private updateState(): void {
    this.authenticated.set(!!this.client.authenticated);
    this.name.set(this.client.tokenParsed?.['name'] ??
      this.client.tokenParsed?.['preferred_username'] ?? 'Keycloak user');
  }

  async accessToken(): Promise<string | undefined> {
    await this.initialize();
    if (!this.client.authenticated) return undefined;
    try {
      await this.client.updateToken(30);
      this.updateState();
      return this.client.token;
    } catch {
      this.client.clearToken();
      this.authenticated.set(false);
      this.error.set('Your session expired. Please sign in again.');
      throw new Error('Could not refresh the authentication session.');
    }
  }

  async signIn(): Promise<void> {
    try {
      await this.client.login({ redirectUri: window.location.origin + '/' });
    } catch {
      this.error.set('Sign in failed. Check the Keycloak client configuration.');
    }
  }

  async signOut(): Promise<void> {
    try {
      await this.client.logout({ redirectUri: window.location.origin + '/' });
    } catch {
      this.error.set('Sign out failed. Please try again.');
    }
  }
}
