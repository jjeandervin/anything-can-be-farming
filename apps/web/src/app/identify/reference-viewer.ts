import { Component, ElementRef, afterNextRender, input, output, viewChild } from '@angular/core';

/** A reference photo ready to display. Every URL here has already been checked to be HTTPS. */
export interface ViewedImage {
  thumbnailUrl: string;
  imageUrl: string;
  fullUrl: string | null;
  organ: string | null;
  alt: string;
  credit: string | null;
  author: string | null;
  license: string | null;
}

@Component({
  selector: 'app-reference-viewer',
  templateUrl: './reference-viewer.html',
  styleUrl: './reference-viewer.css',
  host: { '(document:keydown.escape)': 'closed.emit()' },
})
export class ReferenceViewer {
  readonly image = input.required<ViewedImage>();
  readonly closed = output<void>();
  private readonly closeButton = viewChild.required<ElementRef<HTMLButtonElement>>('close');

  constructor() {
    afterNextRender(() => this.closeButton().nativeElement.focus());
  }

  backdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.closed.emit();
  }
}
