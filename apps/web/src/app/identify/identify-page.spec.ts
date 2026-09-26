import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from '../auth.service';
import { IMAGE_PREPARER, ImagePrepError } from './image-prep';
import { IdentifyResponse, PhotoToIdentify } from './identify.models';
import { IdentifyService } from './identify.service';
import { IdentifyPage } from './identify-page';

describe('IdentifyPage', () => {
  const auth = {
    ready: signal(true), authenticated: signal(true), name: signal('Test User'), error: signal(''),
    initialize: vi.fn(), signIn: vi.fn(), signOut: vi.fn(),
  };
  let responses: Subject<IdentifyResponse>;
  let identify: ReturnType<typeof vi.fn>;
  let prepare: ReturnType<typeof vi.fn>;
  let createObjectURL: ReturnType<typeof vi.fn>;
  let revokeObjectURL: ReturnType<typeof vi.fn>;
  const original = { create: URL.createObjectURL, revoke: URL.revokeObjectURL };
  let fixture: ComponentFixture<IdentifyPage>;
  let element: HTMLElement;

  beforeEach(() => {
    vi.clearAllMocks();
    auth.ready.set(true);
    auth.authenticated.set(true);
    auth.error.set('');
    responses = new Subject<IdentifyResponse>();
    identify = vi.fn((_: PhotoToIdentify[]) => responses);
    prepare = vi.fn(async (file: File) => new File([file.name], 'photo.jpg', { type: 'image/jpeg' }));
    let urls = 0;
    createObjectURL = vi.fn(() => `blob:preview-${++urls}`);
    revokeObjectURL = vi.fn();
    URL.createObjectURL = createObjectURL as typeof URL.createObjectURL;
    URL.revokeObjectURL = revokeObjectURL as typeof URL.revokeObjectURL;
    TestBed.configureTestingModule({ providers: [
      { provide: AuthService, useValue: auth },
      { provide: IdentifyService, useValue: { identify } },
      { provide: IMAGE_PREPARER, useValue: prepare },
    ] });
  });
  afterEach(() => {
    URL.createObjectURL = original.create;
    URL.revokeObjectURL = original.revoke;
  });

  async function render() {
    fixture = TestBed.createComponent(IdentifyPage);
    element = fixture.nativeElement;
    await settle();
  }

  async function settle() {
    for (let i = 0; i < 5; i++) await Promise.resolve();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const photo = (name = 'leaf.jpg') => new File(['raw'], name, { type: 'image/jpeg' });
  const text = () => element.textContent?.replace(/\s+/g, ' ') ?? '';
  const cards = () => [...element.querySelectorAll('.card')];
  const buttonNamed = (name: string) => [...element.querySelectorAll<HTMLButtonElement>('button')]
    .find(b => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name);

  async function choose(...files: File[]) {
    const input = element.querySelector<HTMLInputElement>('input[multiple]')!;
    Object.defineProperty(input, 'files', { value: files, configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle();
  }

  async function submit() {
    buttonNamed('Identify')!.click();
    await settle();
  }

  it('asks signed-out users to sign in and hides the picker', async () => {
    auth.authenticated.set(false);
    await render();
    expect(text()).toContain('Sign in to identify plants');
    expect(element.querySelector('input[type=file]')).toBeNull();
    buttonNamed('Sign in')!.click();
    expect(auth.signIn).toHaveBeenCalled();
  });

  it('shows a checking state until sign-in is known', async () => {
    auth.ready.set(false);
    await render();
    expect(text()).toContain('Checking sign-in…');
    expect(element.querySelector('input[type=file]')).toBeNull();
  });

  it('offers a camera input and a library input', async () => {
    await render();
    const camera = element.querySelector<HTMLInputElement>('input[capture]')!;
    expect(camera.getAttribute('capture')).toBe('environment');
    expect(camera.multiple).toBe(false);
    const library = element.querySelector<HTMLInputElement>('input[multiple]')!;
    expect(library.hasAttribute('capture')).toBe(false);
    for (const input of [camera, library])
      expect(input.accept).toBe('image/jpeg,image/png,image/heic,image/heif,image/*');
    expect(buttonNamed('Add photo')).toBeDefined();
    expect(buttonNamed('Choose from library')).toBeDefined();
  });

  it('prepares added photos and defaults their organ to auto', async () => {
    await render();
    const files = [photo('a.jpg'), photo('b.jpg')];
    await choose(...files);
    expect(prepare.mock.calls.map(call => call[0])).toEqual(files);
    expect(cards()).toHaveLength(2);
    const selects = [...element.querySelectorAll('select')];
    expect(selects.map(s => s.value)).toEqual(['auto', 'auto']);
    expect(selects.map(s => s.labels?.[0]?.textContent?.trim())).toEqual(['Photo 1 shows', 'Photo 2 shows']);
    expect([...selects[0].querySelectorAll('optgroup')].map(g => g.label)).toEqual(['Common', 'Specialist']);
    expect([...element.querySelectorAll<HTMLImageElement>('.card img')].map(i => i.getAttribute('src')))
      .toEqual(['blob:preview-1', 'blob:preview-2']);
  });

  it('shows a spinner while a photo is being prepared', async () => {
    let finish!: (file: File) => void;
    prepare.mockReturnValueOnce(new Promise<File>(resolve => finish = resolve));
    await render();
    await choose(photo());
    expect(element.querySelector('.card .spinner')).not.toBeNull();
    expect(buttonNamed('Identify')!.disabled).toBe(true);
    finish(new File(['x'], 'photo.jpg', { type: 'image/jpeg' }));
    await settle();
    expect(element.querySelector('.card .spinner')).toBeNull();
    expect(buttonNamed('Identify')!.disabled).toBe(false);
  });

  it('does not add a photo that cannot be prepared', async () => {
    prepare.mockRejectedValueOnce(new ImagePrepError("This photo format isn't supported here. Try a JPEG or PNG."));
    await render();
    await choose(photo('IMG_0001.HEIC'));
    expect(cards()).toHaveLength(0);
    expect(text()).toContain("This photo format isn't supported here. Try a JPEG or PNG.");
  });

  it('caps the selection at five photos', async () => {
    await render();
    await choose(photo(), photo());
    await choose(...Array.from({ length: 5 }, (_, i) => photo(`${i}.jpg`)));
    expect(cards()).toHaveLength(5);
    expect(prepare).toHaveBeenCalledTimes(5);
    expect(text()).toContain('Only 5 photos per identification.');
    expect(buttonNamed('Add photo')).toBeUndefined();
    expect(buttonNamed('Choose from library')).toBeUndefined();
  });

  it('removes a photo and revokes its preview URL', async () => {
    await render();
    await choose(photo('a.jpg'), photo('b.jpg'));
    buttonNamed('Remove photo 1')!.click();
    await settle();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:preview-1');
    expect(cards()).toHaveLength(1);
    expect(element.querySelector('.card img')?.getAttribute('src')).toBe('blob:preview-2');
  });

  it('revokes every preview URL when the page is destroyed', async () => {
    await render();
    await choose(photo('a.jpg'), photo('b.jpg'));
    fixture.destroy();
    expect(revokeObjectURL.mock.calls.map(call => call[0])).toEqual(['blob:preview-1', 'blob:preview-2']);
  });

  it('disables Identify when there are no photos', async () => {
    await render();
    expect(buttonNamed('Identify')!.disabled).toBe(true);
    await submit();
    expect(identify).not.toHaveBeenCalled();
  });

  it('sends photos with their organs and shows the loading state', async () => {
    await render();
    await choose(photo('a.jpg'), photo('b.jpg'));
    const select = element.querySelectorAll('select')[1];
    select.value = 'flower';
    select.dispatchEvent(new Event('change'));
    await submit();

    const sent = identify.mock.calls[0][0] as PhotoToIdentify[];
    expect(sent.map(p => p.organ)).toEqual(['auto', 'flower']);
    expect(sent.every(p => p.file.type === 'image/jpeg')).toBe(true);
    const button = element.querySelector<HTMLButtonElement>('button.identify')!;
    expect(button.textContent?.trim()).toBe('Identifying…');
    expect(button.disabled).toBe(true);
    expect([...element.querySelectorAll<HTMLSelectElement>('select')].every(s => s.disabled)).toBe(true);
    expect(buttonNamed('Remove photo 1')!.disabled).toBe(true);
    expect(buttonNamed('Add photo')!.disabled).toBe(true);

    button.click();
    expect(identify).toHaveBeenCalledTimes(1);
  });

  it('shows the server message and keeps the photos after an error', async () => {
    await render();
    await choose(photo());
    await submit();
    responses.error(new HttpErrorResponse({ status: 502, error: {
      error: 'upstream_error', message: 'Plant identification is temporarily unavailable.',
    } }));
    await settle();
    expect(element.querySelector('[role=alert]')?.textContent).toContain('Plant identification is temporarily unavailable.');
    expect(cards()).toHaveLength(1);
    expect(buttonNamed('Identify')!.disabled).toBe(false);
  });

  it('shows a connection message when the server gives no message', async () => {
    await render();
    await choose(photo());
    await submit();
    responses.error(new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') }));
    await settle();
    expect(element.querySelector('[role=alert]')?.textContent)
      .toContain("Couldn't reach the server. Check your connection and try again.");
  });

  it('prompts for a fresh sign-in on 401', async () => {
    await render();
    await choose(photo());
    await submit();
    responses.error(new HttpErrorResponse({ status: 401 }));
    await settle();
    expect(element.querySelector('[role=alert]')?.textContent).toContain('Sign in again');
    buttonNamed('Sign in again')!.click();
    expect(auth.signIn).toHaveBeenCalled();
  });

  it('start over clears photos and results', async () => {
    await render();
    await choose(photo('a.jpg'), photo('b.jpg'));
    await submit();
    responses.next({ bestMatch: null, remainingRequests: null, predictedOrgans: [], results: [] });
    await settle();
    buttonNamed('Start over')!.click();
    await settle();
    expect(cards()).toHaveLength(0);
    expect(revokeObjectURL.mock.calls.map(call => call[0])).toEqual(['blob:preview-1', 'blob:preview-2']);
    expect(buttonNamed('Identify')!.disabled).toBe(true);
    expect(fixture.componentInstance.result()).toBeNull();
    expect(fixture.componentInstance.status()).toBe('idle');
  });

  it('edit photos returns to the editable state with photos kept', async () => {
    await render();
    await choose(photo());
    await submit();
    responses.next({ bestMatch: null, remainingRequests: null, predictedOrgans: [], results: [] });
    await settle();
    buttonNamed('Edit photos')!.click();
    await settle();
    expect(cards()).toHaveLength(1);
    expect(element.querySelector('select')?.disabled).toBe(false);
  });
});
