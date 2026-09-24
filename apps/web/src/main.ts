import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { loadRuntimeConfig, RUNTIME_CONFIG } from './app/runtime-config';

loadRuntimeConfig()
  .then(config => bootstrapApplication(App, {
    ...appConfig,
    providers: [...appConfig.providers, { provide: RUNTIME_CONFIG, useValue: config }],
  }))
  .catch(() => {
    const root = document.querySelector('app-root');
    if (root) root.textContent = 'Unable to start. Check the frontend runtime configuration and reload.';
  });
