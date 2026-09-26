import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from '../auth.service';
import { IMAGE_PREPARER, ImagePrepError } from './image-prep';
import { IdentifyApiError, IdentifyResponse, PhotoEntry, PredictedOrgan } from './identify.models';
import { IdentifyResults, percent } from './identify-results';
import { IdentifyService } from './identify.service';
import { DEFAULT_ORGAN, PLANT_ORGAN_GROUPS, PlantOrgan, organLabel } from './plant-organ';

export const MAX_PHOTOS = 5;
export type IdentifyStatus = 'idle' | 'submitting' | 'results' | 'error';

const NETWORK_ERROR = "Couldn't reach the server. Check your connection and try again.";

@Component({
  selector: 'app-identify-page',
  imports: [IdentifyResults],
  templateUrl: './identify-page.html',
  styleUrl: './identify-page.css',
})
export class IdentifyPage {
  readonly auth = inject(AuthService);
  private readonly identifier = inject(IdentifyService);
  private readonly prepare = inject(IMAGE_PREPARER);
  private readonly destroyRef = inject(DestroyRef);

  readonly organGroups = PLANT_ORGAN_GROUPS;
  readonly maxPhotos = MAX_PHOTOS;
  readonly photos = signal<PhotoEntry[]>([]);
  readonly status = signal<IdentifyStatus>('idle');
  readonly result = signal<IdentifyResponse | null>(null);
  readonly errorMessage = signal('');
  /** Set when the API rejected the token, so the error offers a fresh sign-in. */
  readonly sessionExpired = signal(false);
  /** Non-blocking notices about photo selection, such as the 5-photo cap or an unsupported format. */
  readonly notice = signal('');

  readonly submitting = computed(() => this.status() === 'submitting');
  readonly canAdd = computed(() => !this.submitting() && this.photos().length < MAX_PHOTOS);
  readonly canIdentify = computed(() => {
    const photos = this.photos();
    return !this.submitting() && photos.length > 0 && photos.every(photo => photo.file !== null);
  });

  /** For photos sent as Auto: what Pl@ntNet decided each one shows, by photo index. */
  readonly predictedOrgans = computed(() => {
    const byIndex = new Map<number, PredictedOrgan>();
    for (const prediction of this.result()?.predictedOrgans ?? []) {
      const best = byIndex.get(prediction.imageIndex);
      if (!best || prediction.score > best.score) byIndex.set(prediction.imageIndex, prediction);
    }
    return byIndex;
  });

  private nextId = 1;
  private destroyed = false;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.revokeAll(this.photos());
    });
  }

  addFiles(input: HTMLInputElement): void {
    const files = Array.from(input.files ?? []);
    // Clear the input so choosing the same file again still fires a change event.
    input.value = '';
    if (!files.length || !this.canAdd()) return;
    const slots = MAX_PHOTOS - this.photos().length;
    this.notice.set(files.length > slots ? `Only ${MAX_PHOTOS} photos per identification.` : '');
    for (const file of files.slice(0, slots)) void this.addPhoto(file);
  }

  private async addPhoto(file: File): Promise<void> {
    const id = this.nextId++;
    this.photos.update(photos => [...photos, { id, file: null, previewUrl: null, organ: DEFAULT_ORGAN }]);
    let prepared: File;
    try {
      prepared = await this.prepare(file);
    } catch (error) {
      this.photos.update(photos => photos.filter(photo => photo.id !== id));
      this.notice.set(error instanceof ImagePrepError
        ? error.message
        : "This photo couldn't be prepared. Try another one.");
      return;
    }
    // The photo may have been removed, or the page left, while it was being prepared.
    if (this.destroyed || !this.photos().some(photo => photo.id === id)) return;
    const previewUrl = URL.createObjectURL(prepared);
    this.photos.update(photos => photos.map(photo =>
      photo.id === id ? { ...photo, file: prepared, previewUrl } : photo));
  }

  shortLabel(organ: PlantOrgan): string {
    return organ === 'auto' ? 'Auto' : organLabel(organ);
  }

  sawLabel(prediction: PredictedOrgan): string {
    return `Pl@ntNet saw: ${organLabel(prediction.organ)} (${percent(prediction.score)}%)`;
  }

  setOrgan(id: number, organ: string): void {
    this.photos.update(photos => photos.map(photo =>
      photo.id === id ? { ...photo, organ: organ as PlantOrgan } : photo));
  }

  remove(id: number): void {
    if (this.submitting()) return;
    const photo = this.photos().find(p => p.id === id);
    if (!photo) return;
    this.revokeAll([photo]);
    this.photos.update(photos => photos.filter(p => p.id !== id));
    this.notice.set('');
  }

  identify(): void {
    if (!this.canIdentify()) return;
    const photos = this.photos().map(photo => ({ file: photo.file!, organ: photo.organ }));
    this.status.set('submitting');
    this.errorMessage.set('');
    this.sessionExpired.set(false);
    this.notice.set('');
    this.identifier.identify(photos).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        this.result.set(result);
        this.status.set('results');
      },
      error: (error: unknown) => {
        this.showError(error);
        this.status.set('error');
      },
    });
  }

  /** Back to the editable state with the photos kept. */
  editPhotos(): void {
    this.result.set(null);
    this.status.set('idle');
  }

  startOver(): void {
    this.revokeAll(this.photos());
    this.photos.set([]);
    this.result.set(null);
    this.errorMessage.set('');
    this.sessionExpired.set(false);
    this.notice.set('');
    this.status.set('idle');
  }

  private showError(error: unknown): void {
    if (error instanceof HttpErrorResponse && error.status === 401) {
      this.sessionExpired.set(true);
      this.errorMessage.set('Your session has expired. Sign in again to identify plants.');
      return;
    }
    const body = error instanceof HttpErrorResponse ? error.error as Partial<IdentifyApiError> | null : null;
    this.errorMessage.set(typeof body?.message === 'string' && body.message ? body.message : NETWORK_ERROR);
  }

  private revokeAll(photos: readonly PhotoEntry[]): void {
    for (const photo of photos) if (photo.previewUrl) URL.revokeObjectURL(photo.previewUrl);
  }
}
