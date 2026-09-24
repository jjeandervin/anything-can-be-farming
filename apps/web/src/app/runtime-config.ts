import { InjectionToken } from '@angular/core';

export interface RuntimeConfig {
  keycloakIssuer: string;
  keycloakClientId: string;
  apiBaseUrl: string;
}

export const RUNTIME_CONFIG = new InjectionToken<RuntimeConfig>('runtime configuration');

export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  const response = await fetch('/config.json', { cache: 'no-store' });
  if (!response.ok) throw new Error('Runtime configuration is unavailable.');
  const config: RuntimeConfig = await response.json();
  const issuer = new URL(config.keycloakIssuer);
  const api = new URL(config.apiBaseUrl);
  if (!['http:', 'https:'].includes(api.protocol) ||
      issuer.protocol !== 'https:' || !/\/realms\/[^/]+\/?$/.test(issuer.pathname) ||
      !config.keycloakClientId) {
    throw new Error('Invalid API or Keycloak configuration.');
  }
  return { ...config, apiBaseUrl: config.apiBaseUrl.replace(/\/+$/, '') };
}
