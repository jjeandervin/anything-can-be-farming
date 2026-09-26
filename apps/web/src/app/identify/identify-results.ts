import { Component, computed, input, signal } from '@angular/core';
import { IdentifyResponse, IdentifyResult, ReferenceImage } from './identify.models';
import { organLabel } from './plant-organ';
import { ReferenceViewer, ViewedImage } from './reference-viewer';

export const LOW_CONFIDENCE = 0.2;
export const MUTED_BELOW = 0.1;
export const NAMES_SHOWN = 5;

/** Only HTTPS URLs from the API are ever bound to the page. */
export function isHttps(url: string | null | undefined): url is string {
  return typeof url === 'string' && url.startsWith('https://');
}

export function percent(score: number): number {
  return Math.round(score * 100);
}

interface ResultCard {
  rank: number;
  percent: number;
  barWidth: number;
  muted: boolean;
  title: string;
  /** The italic name under the title, when the title is a common name. */
  italicName: string | null;
  authorship: string | null;
  taxonomy: string | null;
  otherNames: string[];
  images: ViewedImage[];
  credits: string | null;
}

@Component({
  selector: 'app-identify-results',
  imports: [ReferenceViewer],
  templateUrl: './identify-results.html',
  styleUrl: './identify-results.css',
})
export class IdentifyResults {
  readonly result = input.required<IdentifyResponse>();

  readonly namesShown = NAMES_SHOWN;
  readonly expanded = signal<ReadonlySet<number>>(new Set());
  readonly viewing = signal<ViewedImage | null>(null);
  private opener: HTMLElement | null = null;

  readonly cards = computed(() => this.result().results.map(toCard));
  readonly lowConfidence = computed(() => {
    const top = this.result().results[0];
    return !!top && top.score < LOW_CONFIDENCE;
  });

  expand(rank: number): void {
    this.expanded.update(ranks => new Set(ranks).add(rank));
  }

  open(image: ViewedImage, opener: HTMLElement): void {
    this.opener = opener;
    this.viewing.set(image);
  }

  close(): void {
    this.viewing.set(null);
    this.opener?.focus();
    this.opener = null;
  }
}

function toCard(result: IdentifyResult, index: number): ResultCard {
  const commonName = result.commonNames[0];
  const scientific = result.scientificNameWithoutAuthor;
  const images = result.referenceImages
    .filter(image => isHttps(image.thumbnailUrl) && isHttps(image.imageUrl))
    .map(image => toViewed(image, scientific));
  return {
    rank: index + 1,
    percent: percent(result.score),
    barWidth: Math.min(100, Math.max(0, result.score * 100)),
    muted: result.score < MUTED_BELOW,
    title: commonName ?? scientific,
    italicName: commonName ? scientific : null,
    authorship: result.authorship || null,
    taxonomy: [result.family && `Family ${result.family}`, result.genus && `Genus ${result.genus}`]
      .filter(Boolean).join(' · ') || null,
    otherNames: result.commonNames.slice(1),
    images,
    credits: credits(images),
  };
}

function toViewed(image: ReferenceImage, scientificName: string): ViewedImage {
  const organ = image.organ ? organLabel(image.organ) : null;
  return {
    thumbnailUrl: image.thumbnailUrl,
    imageUrl: image.imageUrl,
    fullUrl: isHttps(image.fullUrl) ? image.fullUrl : null,
    organ,
    alt: organ
      ? `Reference photo: ${organ.toLowerCase()} of ${scientificName}`
      : `Reference photo of ${scientificName}`,
    credit: image.citation || [image.author, image.license && `(${image.license})`].filter(Boolean).join(' ') || null,
    author: image.author,
    license: image.license,
  };
}

/** "Photos: Jane Doe (cc-by-sa); Sam Roe (cc-by)", with repeated author/license pairs listed once. */
function credits(images: ViewedImage[]): string | null {
  const seen = new Set<string>();
  const parts: string[] = [];
  for (const { author, license } of images) {
    const part = `${author || 'Unknown author'}${license ? ` (${license})` : ''}`;
    if (!seen.has(part)) {
      seen.add(part);
      parts.push(part);
    }
  }
  return parts.length ? `Photos: ${parts.join('; ')}` : null;
}
