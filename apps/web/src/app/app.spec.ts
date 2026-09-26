import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './app';
import { routes } from './app.routes';
import { AuthService } from './auth.service';
import { RUNTIME_CONFIG } from './runtime-config';

describe('App shell', () => {
  const auth = {
    ready: signal(true), authenticated: signal(false), name: signal('Test User'), error: signal(''),
    initialize: vi.fn().mockResolvedValue(undefined), signIn: vi.fn(), signOut: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    auth.ready.set(true);
    auth.authenticated.set(false);
    TestBed.configureTestingModule({ providers: [
      provideRouter(routes), provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthService, useValue: auth },
      { provide: RUNTIME_CONFIG, useValue: { apiBaseUrl: 'https://localhost:7243' } },
    ] });
  });

  async function render(url: string) {
    const fixture = TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    fixture.detectChanges();
    await router.navigateByUrl(url);
    await fixture.whenStable();
    fixture.detectChanges();
    return { element: fixture.nativeElement as HTMLElement, router };
  }

  function button(element: HTMLElement, text: string) {
    return [...element.querySelectorAll<HTMLButtonElement>('header button')]
      .find(b => b.textContent?.trim() === text);
  }

  it('initializes authentication and shows the header navigation', async () => {
    const { element } = await render('/status');
    expect(auth.initialize).toHaveBeenCalled();
    expect(element.querySelector('.brand')?.textContent).toContain('Anything Can Be Farming');
    expect([...element.querySelectorAll('nav a')].map(a => a.textContent?.trim()))
      .toEqual(['Identify', 'Status']);
    expect(element.querySelector('main app-status-page')).not.toBeNull();
  });

  it.each([
    ['/', '/identify'],
    ['/nowhere', '/identify'],
    ['/status', '/status'],
  ])('routes %s to %s', async (url, expected) => {
    const { router } = await render(url);
    expect(router.url).toBe(expected);
  });

  it('lazy-loads the identify page', async () => {
    const { element } = await render('/identify');
    expect(element.querySelector('main app-identify-page')).not.toBeNull();
  });

  it('marks the active link', async () => {
    const { element } = await render('/status');
    const active = element.querySelector('nav a.active');
    expect(active?.textContent?.trim()).toBe('Status');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('offers sign in when signed out', async () => {
    const { element } = await render('/status');
    button(element, 'Sign in')!.click();
    expect(auth.signIn).toHaveBeenCalled();
  });

  it('shows who is signed in and offers sign out', async () => {
    auth.authenticated.set(true);
    const { element } = await render('/status');
    expect(element.querySelector('header')?.textContent).toContain('Signed in as Test User');
    button(element, 'Sign out')!.click();
    expect(auth.signOut).toHaveBeenCalled();
  });

  it('shows a checking state until authentication is ready', async () => {
    auth.ready.set(false);
    const { element } = await render('/status');
    expect(element.querySelector('header')?.textContent).toContain('Checking sign-in…');
    expect(button(element, 'Sign in')).toBeUndefined();
  });
});
