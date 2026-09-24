import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';
import { AuthService } from './auth.service';
import { RUNTIME_CONFIG } from './runtime-config';

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const config = inject(RUNTIME_CONFIG);
  const target = new URL(request.url, window.location.origin);
  const api = new URL(config.apiBaseUrl);
  const apiPath = api.pathname.replace(/\/$/, '') + '/api/';
  // Exact origin AND path check prevents token leakage to unrelated URLs.
  if (target.origin !== api.origin || !target.pathname.startsWith(apiPath) ||
      target.pathname === apiPath + 'status' || target.pathname === apiPath + 'status/database') {
    return next(request);
  }
  const auth = inject(AuthService);
  return from(auth.accessToken()).pipe(switchMap(token =>
    next(token ? request.clone({ setHeaders: { Authorization: 'Bearer ' + token } }) : request)));
};
