import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RecipeStatus } from '../../../core/models/recipe.models';
import { CreateShareLinkRequest, ShareLinkDto } from '../../../core/models/share-link.models';
import { ShareLinkService } from '../../../core/services/share-link/share-link.service';

export interface RecipeShareDialogData {
  recipeId: string;
  recipeTitle: string;
  recipeStatus: RecipeStatus;
}

export type ExpirationPreset = 'never' | '24h' | '7d' | '30d';

@Component({
  imports: [
    ReactiveFormsModule,
    DatePipe,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
    MatDividerModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-recipe-share-dialog',
  templateUrl: './recipe-share-dialog.component.html',
  styleUrl: './recipe-share-dialog.component.scss',
})
export class RecipeShareDialogComponent implements OnInit {
  protected readonly data = inject<RecipeShareDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<RecipeShareDialogComponent>);
  private readonly shareLinkService = inject(ShareLinkService);
  private readonly fb = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly shareLinks = signal<ShareLinkDto[]>([]);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly isSubmitting = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    label: ['', [Validators.maxLength(120)]],
    expirationPreset: ['never' as ExpirationPreset],
    includeCookLogs: [false],
  });

  ngOnInit(): void {
    this.loadShareLinks();
  }

  loadShareLinks(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.shareLinkService.getShareLinks(this.data.recipeId).subscribe({
      next: (links) => {
        this.shareLinks.set(links);
        this.isLoading.set(false);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isLoading.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to load share links.',
        );
      },
    });
  }

  onCreateShareLink(): void {
    if (this.data.recipeStatus !== 'Published' || this.form.invalid || this.isSubmitting()) {
      return;
    }

    this.isSubmitting.set(true);
    const formVal = this.form.getRawValue();

    let expiresAt: string | null = null;
    if (formVal.expirationPreset !== 'never') {
      const now = new Date();
      if (formVal.expirationPreset === '24h') {
        now.setHours(now.getHours() + 24);
      } else if (formVal.expirationPreset === '7d') {
        now.setDate(now.getDate() + 7);
      } else if (formVal.expirationPreset === '30d') {
        now.setDate(now.getDate() + 30);
      }
      expiresAt = now.toISOString();
    }

    const request: CreateShareLinkRequest = {
      label: formVal.label.trim() || null,
      includeCookLogs: formVal.includeCookLogs,
      expiresAt,
    };

    this.shareLinkService.createShareLink(this.data.recipeId, request).subscribe({
      next: (created) => {
        this.isSubmitting.set(false);
        this.shareLinks.update((current) => [created, ...current]);
        this.form.reset({
          label: '',
          expirationPreset: 'never',
          includeCookLogs: false,
        });

        const shareUrl = this.buildShareUrl(created.token);
        this.copyToClipboard(shareUrl, 'Share link created and copied to clipboard!');
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isSubmitting.set(false);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to create share link.',
          'Close',
          { duration: 5000 },
        );
      },
    });
  }

  onRevoke(link: ShareLinkDto): void {
    this.shareLinkService.revokeShareLink(this.data.recipeId, link.id).subscribe({
      next: (updated) => {
        this.shareLinks.update((current) =>
          current.map((item) => (item.id === updated.id ? updated : item)),
        );
        this.snackBar.open('Share link revoked.', 'Close', { duration: 3000 });
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to revoke link.',
          'Close',
          { duration: 5000 },
        );
      },
    });
  }

  onDelete(link: ShareLinkDto): void {
    this.shareLinkService.deleteShareLink(this.data.recipeId, link.id).subscribe({
      next: () => {
        this.shareLinks.update((current) => current.filter((item) => item.id !== link.id));
        this.snackBar.open('Share link deleted.', 'Close', { duration: 3000 });
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to delete link.',
          'Close',
          { duration: 5000 },
        );
      },
    });
  }

  buildShareUrl(token: string): string {
    return this.shareLinkService.buildPublicShareUrl(token);
  }

  copyLink(token: string): void {
    const url = this.buildShareUrl(token);
    this.copyToClipboard(url, 'Share link copied to clipboard!');
  }

  private copyToClipboard(text: string, successMessage: string): void {
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      navigator.clipboard.writeText(text).then(
        () => {
          this.snackBar.open(successMessage, 'Close', { duration: 3000 });
        },
        () => {
          this.snackBar.open(text, 'Close', { duration: 6000 });
        },
      );
    } else {
      this.snackBar.open(text, 'Close', { duration: 6000 });
    }
  }

  close(): void {
    this.dialogRef.close();
  }
}
