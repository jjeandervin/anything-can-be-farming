import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom, timeout } from 'rxjs';
import { AuthService } from './auth.service';
import { RUNTIME_CONFIG } from './runtime-config';

interface CurrentUser {
  authenticated: boolean;
  subject: string;
  username: string | null;
  displayName: string | null;
}

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly config = inject(RUNTIME_CONFIG);
  readonly api = signal('Checking…');
  readonly database = signal('Checking…');
  readonly protectedApi = signal('Sign in to verify');
  readonly checking = signal(false);

  ngOnInit(): void {
    void this.refresh();
    void this.auth.initialize().then(() => this.checkIdentity());
  }

  async refresh(): Promise<void> {
    this.checking.set(true);
    await Promise.all([
      this.check('/api/status', this.api),
      this.check('/api/status/database', this.database),
    ]);
    if (this.auth.ready()) await this.checkIdentity();
    this.checking.set(false);
  }

  private async check(path: string, state: ReturnType<typeof signal<string>>): Promise<void> {
    state.set('Checking…');
    try {
      await firstValueFrom(this.http.get(this.config.apiBaseUrl + path).pipe(timeout(8000)));
      state.set('Connected');
    } catch {
      state.set('Unavailable');
    }
  }

  private async checkIdentity(): Promise<void> {
    if (!this.auth.authenticated()) {
      this.protectedApi.set('Sign in to verify');
      return;
    }
    this.protectedApi.set('Checking…');
    try {
      const user = await firstValueFrom(
        this.http.get<CurrentUser>(this.config.apiBaseUrl + '/api/auth/me').pipe(timeout(15000)));
      this.protectedApi.set('Verified as ' + (user.displayName || user.username || user.subject));
    } catch (error) {
      this.protectedApi.set(error instanceof HttpErrorResponse && error.status === 401
        ? 'Token rejected — check issuer and API audience'
        : 'Verification unavailable');
    }
  }
}
