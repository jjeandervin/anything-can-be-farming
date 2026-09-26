import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, timeout } from 'rxjs';
import { RUNTIME_CONFIG } from '../runtime-config';
import { IdentifyResponse, PhotoToIdentify } from './identify.models';

export const IDENTIFY_TIMEOUT_MS = 45_000;

@Injectable({ providedIn: 'root' })
export class IdentifyService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(RUNTIME_CONFIG);

  identify(photos: readonly PhotoToIdentify[]): Observable<IdentifyResponse> {
    const form = new FormData();
    // The API pairs images and organs by position, so both lists keep the photos' order.
    for (const photo of photos) form.append('images', photo.file, photo.file.name);
    for (const photo of photos) form.append('organs', photo.organ);
    // No Content-Type header: the browser sets multipart/form-data with its boundary.
    return this.http
      .post<IdentifyResponse>(`${this.config.apiBaseUrl}/api/identify`, form)
      .pipe(timeout(IDENTIFY_TIMEOUT_MS));
  }
}
