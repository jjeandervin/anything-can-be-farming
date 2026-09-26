import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { IdentifyResponse, IdentifyResult, ReferenceImage } from './identify.models';
import { IdentifyResults } from './identify-results';

describe('IdentifyResults', () => {
  let fixture: ComponentFixture<IdentifyResults>;
  let element: HTMLElement;

  const image = (overrides: Partial<ReferenceImage> = {}): ReferenceImage => ({
    organ: 'habit', thumbnailUrl: 'https://bs.plantnet.org/s/1', imageUrl: 'https://bs.plantnet.org/m/1',
    fullUrl: 'https://bs.plantnet.org/o/1', author: 'Jane Doe', license: 'cc-by-sa',
    citation: 'Jane Doe / Pl@ntNet, cc-by-sa', ...overrides,
  });
  const match = (overrides: Partial<IdentifyResult> = {}): IdentifyResult => ({
    score: 0.5, scientificName: 'Acer palmatum Thunb.', scientificNameWithoutAuthor: 'Acer palmatum',
    authorship: 'Thunb.', genus: null, family: null, commonNames: [], gbifId: null, powoId: null,
    referenceImages: [image()], ...overrides,
  });

  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(results: IdentifyResult[]) {
    const response: IdentifyResponse = { bestMatch: null, remainingRequests: null, predictedOrgans: [], results };
    fixture = TestBed.createComponent(IdentifyResults);
    fixture.componentRef.setInput('result', response);
    element = fixture.nativeElement;
    await settle();
  }

  async function settle() {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const viewer = () => element.querySelector('[role=dialog]');

  it('renders reference thumbnails lazily, without a referrer, and with descriptive alt text', async () => {
    await render([match()]);
    const img = element.querySelector<HTMLImageElement>('.refs img')!;
    expect(img.getAttribute('src')).toBe('https://bs.plantnet.org/s/1');
    expect(img.getAttribute('loading')).toBe('lazy');
    expect(img.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(img.alt).toBe('Reference photo: whole plant of Acer palmatum');
  });

  it('skips reference images whose URLs are not HTTPS', async () => {
    await render([match({ referenceImages: [
      image({ thumbnailUrl: 'http://insecure.example/s', author: 'Insecure' }),
      image({ imageUrl: 'javascript:alert(1)', author: 'Script' }),
      image({ author: 'Kept' }),
    ] })]);
    expect(element.querySelectorAll('.refs img')).toHaveLength(1);
    expect(element.querySelector('.credits')?.textContent).toBe('Photos: Kept (cc-by-sa)');
  });

  it('opens a viewer with the image, organ, credit, and full-size link', async () => {
    await render([match()]);
    element.querySelector<HTMLButtonElement>('.ref')!.click();
    await settle();

    const dialog = viewer()!;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.querySelector('img')?.getAttribute('src')).toBe('https://bs.plantnet.org/m/1');
    expect(dialog.querySelector('h2')?.textContent).toBe('Whole plant');
    expect(dialog.querySelector('.credit')?.textContent).toBe('Jane Doe / Pl@ntNet, cc-by-sa');
    const link = dialog.querySelector('a')!;
    expect(link.textContent).toBe('Open full size');
    expect(link.getAttribute('href')).toBe('https://bs.plantnet.org/o/1');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener noreferrer');
    expect(document.activeElement).toBe(dialog.querySelector('.close'));
  });

  it('closes the viewer with Esc and returns focus to the thumbnail', async () => {
    await render([match()]);
    const thumbnail = element.querySelector<HTMLButtonElement>('.ref')!;
    document.body.appendChild(element);
    thumbnail.focus();
    thumbnail.click();
    await settle();
    expect(viewer()).not.toBeNull();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle();
    expect(viewer()).toBeNull();
    expect(document.activeElement).toBe(thumbnail);
    element.remove();
  });

  it('closes the viewer with the close button', async () => {
    await render([match()]);
    element.querySelector<HTMLButtonElement>('.ref')!.click();
    await settle();
    viewer()!.querySelector<HTMLButtonElement>('.close')!.click();
    await settle();
    expect(viewer()).toBeNull();
  });

  it('builds a credit line when there is no citation and omits a non-HTTPS full-size link', async () => {
    await render([match({ referenceImages: [image({ citation: null, organ: null, fullUrl: 'http://x.example/o' })] })]);
    element.querySelector<HTMLButtonElement>('.ref')!.click();
    await settle();
    expect(viewer()!.querySelector('.credit')?.textContent).toBe('Jane Doe (cc-by-sa)');
    expect(viewer()!.querySelector('h2')?.textContent).toBe('Reference photo');
    expect(viewer()!.querySelector('a')).toBeNull();
  });

  it('shows at most five other names, then expands the rest inline', async () => {
    const names = ['Main', 'One', 'Two', 'Three', 'Four', 'Five', 'Six', 'Seven'];
    await render([match({ commonNames: names })]);
    const also = () => element.querySelector('.also')!.textContent!.replace(/\s+/g, ' ').trim();
    expect(also()).toBe('Also called: One, Two, Three, Four, Five +2 more');
    element.querySelector<HTMLButtonElement>('.more')!.click();
    await settle();
    expect(also()).toBe('Also called: One, Two, Three, Four, Five, Six, Seven');
  });
});
