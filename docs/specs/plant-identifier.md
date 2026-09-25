# Spec: Plant identifier (Pl@ntNet), v1

## Goal

From a phone, sign in, take or pick 1–5 photos of **one** plant, label each photo with the plant organ it shows, and see every identification Pl@ntNet returns, including its reference photos. Nothing is saved. This is the first real feature page of the app.

## Out of scope for v1

- Saving photos or results, or linking a result to the plant inventory
- Matching results to WFO taxa
- Disease or variety identification, quota or subscription pages
- Storing location, maps, or offline support

Do not add database tables or migrations for this feature.

## Existing code to build on

- `src/AnythingCanBeFarming.Api/PlantNet/PlantNetClient.cs`. `IdentifyAsync(images, project, options, ct)` already validates 1–5 JPEG/PNG images and one organ per image, keeps the API key out of logs and exceptions, and throws `HttpRequestException` (with `StatusCode`) on upstream failures. It throws `InvalidOperationException` when `PlantNet:ApiKey` is not configured. **Do not modify its public surface** unless a change below requires it.
- `PlantNetResponses.cs` contains `PlantNetIdentificationResult`, `PlantNetMatch`, `PlantNetImage`, `PlantNetPredictedOrgan`, and `PlantNetGbif` (`double Id`).
- Auth: JWT bearer via Keycloak. The Angular `authInterceptor` attaches tokens to API calls, and `AuthService` exposes `ready()`, `authenticated()`, `signIn()`, `signOut()`, and `name()`.
- The frontend is Angular 22 with standalone components, signals, and control-flow syntax. There is no router yet. Tests run on Vitest.

---

## 1. Organ enum (shared vocabulary)

The enum covers all 15 organ values the client accepts. The wire values are the exact lowercase strings.

| Wire value | UI label | UI group |
|---|---|---|
| `auto` | Auto (let Pl@ntNet decide) | Common |
| `leaf` | Leaf | Common |
| `flower` | Flower | Common |
| `fruit` | Fruit | Common |
| `bark` | Bark | Common |
| `habit` | Whole plant | Common |
| `branch` | Branch | Common |
| `bud` | Bud | Common |
| `seed` | Seed | Common |
| `other` | Other | Common |
| `scan` | Scan | Specialist |
| `sheet` | Herbarium sheet | Specialist |
| `drawing` | Drawing | Specialist |
| `anatomy` | Anatomy (microscope) | Specialist |
| `aerial` | Aerial view | Specialist |

### Backend

- Add `public enum PlantOrgan { Auto, Leaf, Flower, Fruit, Bark, Habit, Scan, Branch, Sheet, Other, Drawing, Seed, Bud, Anatomy, Aerial }` in `src/AnythingCanBeFarming.Api/Identification/PlantOrgan.cs`.
- Add a helper `PlantOrgans.TryParse(string value, out PlantOrgan organ)`. It accepts **only** the exact lowercase wire value: reject `"Leaf"`, `" leaf"`, and numeric strings such as `"1"`. Use an explicit mapping, not `Enum.TryParse`, which accepts numbers.
- Add `PlantOrgans.ToWire(PlantOrgan)`, which returns the lowercase string passed to `PlantNetClient`.
- Add a unit test asserting the enum's wire values are exactly the set in `PlantNetClient.ValidOrgans`. Expose that set as `internal` with `InternalsVisibleTo` to the test project, or duplicate the list in the test. Either way, the test must fail if the two ever drift.

### Frontend

- `apps/web/src/app/identify/plant-organ.ts`:
  - `export const PLANT_ORGANS = [...] as const`, with `{ value, label, group }` entries in the table order above.
  - `export type PlantOrgan = typeof PLANT_ORGANS[number]['value']`
  - `DEFAULT_ORGAN: PlantOrgan = 'auto'`

---

## 2. API endpoint

### `POST /api/identify`

- New `IdentifyController` in `src/AnythingCanBeFarming.Api/Controllers/`. It has `[ApiController]`, `[Authorize]`, and `[Route("api/identify")]`.
- `Consumes("multipart/form-data")`.
- Request limits: `[RequestSizeLimit(30 * 1024 * 1024)]` and `[RequestFormLimits(MultipartBodyLengthLimit = 30 * 1024 * 1024)]`.

**Form fields**

| Field | Type | Rules |
|---|---|---|
| `images` | 1–5 files (repeated field) | Each ≤ 8 MiB and non-empty. The content must be JPEG or PNG, **verified by magic bytes** (`FF D8 FF` or `89 50 4E 47 0D 0A 1A 0A`). Do not trust the client's content type or file name. |
| `organs` | strings (repeated field), same count and order as `images` | Each must parse with `PlantOrgans.TryParse`. |

**Validation failures** return `400` with `{ "error": "<code>", "message": "<human text>" }`. The codes are `no_images`, `too_many_images`, `organ_count_mismatch`, `invalid_organ`, `image_too_large`, `unsupported_image`, and `empty_image`.

### Behavior

1. Read each file into memory and build `PlantNetImageUpload` objects. The content type comes from the magic-byte sniff. Rename each file to `image-{index}.jpg` or `.png`, with a 0-based index, so predicted organs can be mapped back to photos reliably.
2. Call `plantNet.IdentifyAsync(images, "all", new PlantNetPlantIdentificationOptions { Language = "en", IncludeRelatedImages = true, Organs = organWireValues }, HttpContext.RequestAborted)`.
   - **Do not set `NumberOfResults`.** Return every match Pl@ntNet sends.
3. Map the result to the response DTO below. Never return Pl@ntNet's raw JSON.
4. Do not persist images or results. Do not log image bytes, file names from the client, or the response body.

### Response `200`

```jsonc
{
  "bestMatch": "Acer palmatum Thunb.",   // PlantNetIdentificationResult.BestMatch, nullable
  "remainingRequests": 487,              // RemainingIdentificationRequests as int, nullable
  "predictedOrgans": [                   // from PredictedOrgans, mapped by filename to index; omit entries that can't be mapped
    { "imageIndex": 0, "organ": "leaf", "score": 0.93 }
  ],
  "results": [                           // same order as Pl@ntNet (score desc); may be empty
    {
      "score": 0.8123,                   // 0–1; null scores are dropped from the list
      "scientificName": "Acer palmatum Thunb.",
      "scientificNameWithoutAuthor": "Acer palmatum",
      "authorship": "Thunb.",
      "genus": "Acer",                   // Species.Genus.ScientificNameWithoutAuthor ?? ScientificName
      "family": "Sapindaceae",           // same rule as genus
      "commonNames": ["Japanese maple", "Smooth Japanese maple"],   // never null, may be empty
      "gbifId": 3189846,                 // long, nullable
      "powoId": "783213-1",              // nullable
      "referenceImages": [               // never null, may be empty
        {
          "organ": "leaf",
          "thumbnailUrl": "https://…/s/…jpg",   // Url.S ?? Url.M
          "imageUrl": "https://…/m/…jpg",       // Url.M ?? Url.O
          "fullUrl": "https://…/o/…jpg",        // Url.O ?? Url.M
          "author": "Jane Doe",
          "license": "cc-by-sa",
          "citation": "Jane Doe / Pl@ntNet, cc-by-sa"
        }
      ]
    }
  ]
}
```

- Put the DTOs in `src/AnythingCanBeFarming.Api/Identification/IdentifyResponse.cs` as records.
- Drop reference images that have no HTTPS URL.
- Drop results with a null `Species` or a null or empty scientific name.

### Error mapping

| Condition | Status | Body `error` | Message |
|---|---|---|---|
| `InvalidOperationException` from the client (key not configured) | 503 | `identification_unavailable` | Plant identification isn't configured. |
| `HttpRequestException` with status 404 (Pl@ntNet found no plant) | **200** | — | Return the normal shape with `results: []`. A no-match is a valid answer, not an error. |
| Upstream 429 | 429 | `quota_exceeded` | Daily identification limit reached. Try again tomorrow. |
| Upstream 400, 413, or 415 | 502 | `upstream_rejected` | Pl@ntNet couldn't process these photos. |
| Any other upstream failure, a timeout, or bad JSON | 502 | `upstream_error` | Plant identification is temporarily unavailable. |
| Client disconnected (`OperationCanceledException` with `RequestAborted` set) | let it propagate | — | — |

- Add a 30-second timeout on the `PlantNetClient` HttpClient (in `AddPlantNet`). The default is 100 s.
- A `TaskCanceledException` caused by that timeout (not by the request abort) maps to `upstream_error`.

### Backend tests

Add these in `tests/AnythingCanBeFarming.Api.Tests/IdentifyControllerTests.cs`. Use the existing `WebApplicationFactory` and test-token pattern, and swap the Pl@ntNet transport with `ConfigurePrimaryHttpMessageHandler` and a stub handler, like `PlantNetClientTests`.

- 401 without a token.
- Each 400 code above.
- JPEG and PNG accepted. A GIF, or a text file labeled `image/jpeg`, is rejected.
- Organs forwarded in order.
- Files renamed to `image-{n}`.
- `include-related-images=true` and `lang=en` present in the query; `nb-results` absent.
- Full mapping from a realistic Pl@ntNet JSON fixture, including null-safe handling: missing genus or family, no images, null score.
- Predicted organs mapped to indexes.
- Upstream 404 → 200 with empty results; 429 → 429; 500 → 502; missing key → 503.
- The enum drift test from section 1.

---

## 3. Frontend: routing and shell

- Add `provideRouter(routes, withComponentInputBinding())` to `app.config.ts`.
- Routes:
  - `''` redirects to `identify`
  - `identify` → `IdentifyPage` (lazy `loadComponent`)
  - `status` → `StatusPage`
  - `**` redirects to `identify`
- Move the current status-check UI from `App` into `apps/web/src/app/status/status-page.ts` (plus its HTML and CSS) unchanged in behavior. Update its existing tests and imports.
- `App` becomes the shell:
  - A compact header with the app name.
  - Nav links **Identify** and **Status**, with `routerLinkActive` styling.
  - The sign-in/out button, or "Signed in as …".
  - A `<router-outlet>`.
  - Header and nav must work at 360 px width.

---

## 4. Frontend: Identify page

Files live under `apps/web/src/app/identify/`: `identify-page.ts`, `.html`, `.css`, `identify.service.ts`, `image-prep.ts`, `plant-organ.ts`, `identify.models.ts`, and specs.

### Signed-out state

If `auth.ready()` and `!auth.authenticated()`, show "Sign in to identify plants" and a Sign in button. Don't render the photo picker. While `!auth.ready()`, show "Checking sign-in…".

### Photo selection

- Use a visually styled button, **Add photo**, backed by a hidden `<input type="file" accept="image/jpeg,image/png,image/heic,image/heif,image/*" capture="environment">`.
  - `capture` opens the rear camera on phones.
  - Also provide a second button, **Choose from library**, using an input **without** `capture`. Some browsers only offer the camera when `capture` is present.
  - Allow `multiple` on the library input, capped at the remaining slots. Ignore extras and show "Only 5 photos per identification."
- Keep a list of up to **5** photo entries: `{ id, file (prepared JPEG), previewUrl, organ: PlantOrgan }`. The organ defaults to `'auto'`.
- Hide or disable both add buttons at 5 photos.
- Each entry renders as a card with:
  - The thumbnail (square, `object-fit: cover`).
  - A `<select>` bound to its organ, using `<optgroup label="Common">` and `<optgroup label="Specialist">`. The accessible label is "Photo N shows".
  - A **Remove** button with the accessible label "Remove photo N".
- Preview URLs come from `URL.createObjectURL` on the **prepared** blob. Revoke them on remove, on reset, and when the component is destroyed.

### Image preparation (`image-prep.ts`)

Export `prepareImage(file: File): Promise<File>`:

1. Decode with `createImageBitmap(file, { imageOrientation: 'from-image' })` so EXIF rotation is applied.
2. If decoding fails (for example, HEIC on a browser that can't decode it), throw `ImagePrepError('This photo format isn't supported here. Try a JPEG or PNG.')`.
3. Scale so the long edge is at most **1600 px**. Never upscale.
4. Draw to a canvas: `OffscreenCanvas` when available, otherwise a `<canvas>`. Export JPEG at quality **0.85**.
5. Return a new `File([blob], 'photo.jpg', { type: 'image/jpeg' })`.

Re-encoding always strips EXIF metadata, including GPS coordinates, and converts PNG and HEIC to JPEG. This happens even for small images.

- Prepare each photo as soon as it's added, not at submit time.
- Show a small spinner on that card while it's preparing.
- If preparation fails, don't add the card; show the error message instead.

### Submitting

- The **Identify** button is primary and full-width on mobile. It's enabled only when there's at least 1 photo, all photos are prepared, and nothing is in flight.
- `IdentifyService.identify(photos)` builds a `FormData`, appending `images` and `organs` in the same order, and POSTs it to `${apiBaseUrl}/api/identify`. It uses `HttpClient`; the existing interceptor adds the token. It does not set `Content-Type` itself.
- Client timeout: 45 s.
- During the request, disable all inputs and show "Identifying…". Prevent double submits.
- Page state uses signals: `status: 'idle' | 'submitting' | 'results' | 'error'`, `result`, and `errorMessage`.
- Error display:
  - Show the server `message` when the body has one.
  - Otherwise show "Couldn't reach the server. Check your connection and try again."
  - On 401, prompt the user to sign in again.
  - Photos stay in place so the user can retry.

### Results

- Keep the photo strip visible above the results in a compact form (small thumbnails with organ labels).
- Controls:
  - **Start over** clears photos and results.
  - **Edit photos** returns to the editable state with photos kept.
- If an entry was `auto` and `predictedOrgans` has that `imageIndex`, show "Pl@ntNet saw: Leaf (93%)" under its thumbnail, using the organ label from `PLANT_ORGANS`.
- **Empty results:** show "No match found. Try a closer, sharper photo of a leaf or flower, or add another angle."
- **All results** render as a vertical list of cards in the returned order. Each card has:
  - A rank number and the confidence as `Math.round(score * 100)%`, with a thin horizontal bar at that width.
  - A title: the first common name if one exists, otherwise `scientificNameWithoutAuthor`.
  - A subtitle: `scientificNameWithoutAuthor` in italics, then the authorship in normal weight. Omit the subtitle's italic name when the title already is the scientific name, and show only the authorship.
  - A line with the family and genus.
  - Any additional common names as a comma-separated line: "Also called: …". Show at most 5, then "+N more", which expands inline on tap.
  - Reference images as a horizontally scrollable row of thumbnails (`thumbnailUrl`, 96 px square, `loading="lazy"`, `referrerpolicy="no-referrer"`, and alt text "Reference photo: {organ} of {scientific name}").
    - Tapping a thumbnail opens `imageUrl` in a lightweight in-page viewer (a modal with the image, an organ label, and the full credit line). The viewer has a close button, supports Esc, and has a link "Open full size" → `fullUrl` with `target="_blank" rel="noopener noreferrer"`.
    - Under each thumbnail row, show credits compactly: "Photos: {author} ({license})". Deduplicate repeated author/license pairs. Credits are required.
  - Cards with confidence below 10% get muted styling but still show.
- **Low-confidence hint:** if the top score is below 0.20, show a banner above the list: "Low confidence. More photos from different angles, especially a flower or leaf close-up, usually help."
- **Footer:** "Identification by Pl@ntNet". Also show "{remainingRequests} identifications left today" when present.

### Rendering and security

- Render all text from the API with Angular interpolation only. Do not use `innerHTML` or `bypassSecurityTrust*`.
- Only bind image and link URLs that start with `https://`. Skip anything else.

### Layout

- Mobile-first, single column, usable at 360 px width.
- Tap targets are at least 44 px.
- On wider screens, max content width is 720 px, centered.
- Match the existing `styles.css` look.

### Frontend tests (Vitest)

- `plant-organ.spec.ts`: the 15 values are unique, and `DEFAULT_ORGAN` is `'auto'`.
- `identify.service.spec.ts`: `FormData` has `images` and `organs` in order with matching counts; the service posts to the correct URL. Use `HttpTestingController`.
- `image-prep.spec.ts`: scaling math, with the long-edge calculation extracted as a pure function (`fitWithin(w, h, max)`) and tested for landscape, portrait, square, and no-upscale cases. Decode failure throws `ImagePrepError` (mock `createImageBitmap`).
- `identify-page.spec.ts`:
  - Signed-out view.
  - Adding photos defaults to `auto`.
  - The 5-photo cap.
  - Remove revokes the object URL.
  - Identify is disabled when empty.
  - Submitting shows the loading state.
  - Results render all cards, the percentage, the common-name title fallback, the credits line, and the low-confidence banner.
  - The empty-results message.
  - Error message display and photos kept after an error.
  - Start over clears everything.

---

## 5. Acceptance checklist

- [ ] On a phone, signed in: Add photo opens the camera. Taking a leaf photo, leaving Auto, and tapping Identify shows a ranked list of **every** result with percentages, names, and reference photos with credits.
- [ ] Choosing Flower for a flower photo sends `flower` for that image, which is visible in the API tests.
- [ ] A 5th photo is the maximum. Remove works.
- [ ] An iPhone HEIC photo either uploads as a JPEG or shows the "format isn't supported" message. It never sends HEIC to the API.
- [ ] Uploaded images contain no EXIF GPS data.
- [ ] Without `PlantNet:ApiKey` set, the page shows "Plant identification isn't configured."
- [ ] A photo of a non-plant shows the no-match message, not an error.
- [ ] `/status` still works exactly as the old home page did.
- [ ] `dotnet build`, `dotnet test`, `npm run build`, and `npm test -- --watch=false` all pass with no new warnings.
- [ ] The README gets a short "Plant identifier" section with the endpoint and how to try it.

## 6. Suggested implementation order

Do each step as its own commit, and verify it before moving on.

1. Organ enum, helper, and drift test.
2. `POST /api/identify`, DTOs, error mapping, client timeout, and controller tests.
3. Router and shell; move the status page and update its tests.
4. `image-prep.ts` and `plant-organ.ts` with tests.
5. `IdentifyService` and the Identify page photo selection and submit flow, with tests.
6. Results UI, viewer, credits, and hints, with tests.
7. README section, then run the acceptance checklist.
