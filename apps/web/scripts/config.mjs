import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';

const defaults = JSON.parse(readFileSync(new URL('../config.example.json', import.meta.url), 'utf8'));
const config = {
  keycloakIssuer: process.env.ACBF_KEYCLOAK_ISSUER || defaults.keycloakIssuer,
  keycloakClientId: process.env.ACBF_KEYCLOAK_CLIENT_ID || defaults.keycloakClientId,
  apiBaseUrl: process.env.ACBF_API_BASE_URL || defaults.apiBaseUrl,
};
for (const value of [config.keycloakIssuer, config.apiBaseUrl]) {
  const url = new URL(value);
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error('Expected an HTTP(S) URL.');
}
mkdirSync(new URL('../public/', import.meta.url), { recursive: true });
writeFileSync(new URL('../public/config.json', import.meta.url), JSON.stringify(config, null, 2) + '\n');
