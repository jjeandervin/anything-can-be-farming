import { PlantOrgan } from './plant-organ';

/** Response of POST /api/identify. */
export interface IdentifyResponse {
  bestMatch: string | null;
  remainingRequests: number | null;
  predictedOrgans: PredictedOrgan[];
  results: IdentifyResult[];
}

export interface PredictedOrgan {
  imageIndex: number;
  organ: string;
  score: number;
}

export interface IdentifyResult {
  score: number;
  scientificName: string;
  scientificNameWithoutAuthor: string;
  authorship: string | null;
  genus: string | null;
  family: string | null;
  commonNames: string[];
  gbifId: number | null;
  powoId: string | null;
  referenceImages: ReferenceImage[];
}

export interface ReferenceImage {
  organ: string | null;
  thumbnailUrl: string;
  imageUrl: string;
  fullUrl: string;
  author: string | null;
  license: string | null;
  citation: string | null;
}

/** Body of every non-2xx response from POST /api/identify. */
export interface IdentifyApiError {
  error: string;
  message: string;
}

export interface PhotoToIdentify {
  file: File;
  organ: PlantOrgan;
}

/** A photo on the page. `file` and `previewUrl` are null while the photo is being prepared. */
export interface PhotoEntry {
  id: number;
  file: File | null;
  previewUrl: string | null;
  organ: PlantOrgan;
}
