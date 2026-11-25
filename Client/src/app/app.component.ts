import { Component, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Router, RouterOutlet } from '@angular/router';
import { KitchenReferenceService } from './core/services/kitchen-reference/kitchen-reference.service';

@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  styleUrl: './app.component.scss',
  templateUrl: './app.component.html',
  host: {
    '(window:keydown)': 'onGlobalKeydown($event)',
  },
})
export class App {
  private readonly kitchenReferenceService = inject(KitchenReferenceService);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog, { optional: true });

  onGlobalKeydown(event: KeyboardEvent): void {
    if (this.router.url.startsWith('/login')) {
      return;
    }

    if (
      (event.ctrlKey || event.metaKey) &&
      !event.altKey &&
      (event.key === '/' || event.code === 'Slash')
    ) {
      event.preventDefault();
      this.kitchenReferenceService.toggleDialog();
      return;
    }

    if (event.key === '?' && !event.ctrlKey && !event.metaKey && !event.altKey) {
      if (this.dialog && this.dialog.openDialogs.length > 0) {
        return;
      }
      const target = event.target as HTMLElement | null;
      if (
        target &&
        (target.tagName === 'INPUT' ||
          target.tagName === 'TEXTAREA' ||
          target.tagName === 'SELECT' ||
          target.isContentEditable ||
          Boolean(target.closest('[contenteditable="true"]')))
      ) {
        return;
      }
      event.preventDefault();
      this.kitchenReferenceService.openDialog();
    }
  }
}
