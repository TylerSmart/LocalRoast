import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import {
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ImportAppDataBackupResultDto } from '../../../core/models/account.models';
import { AccountService } from '../../../core/services/account/account.service';

export interface ImportAppDataDialogResult {
  success: boolean;
  result?: ImportAppDataBackupResultDto;
}

@Component({
  selector: 'app-import-app-data-dialog',
  imports: [
    FormsModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './import-app-data-dialog.component.html',
  styleUrl: './import-app-data-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ImportAppDataDialogComponent {
  private readonly dialogRef = inject(MatDialogRef<ImportAppDataDialogComponent>);
  private readonly accountService = inject(AccountService);

  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  readonly selectedFile = signal<File | null>(null);
  readonly confirmationText = signal<string>('');
  readonly isImporting = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);

  readonly isConfirmed = computed<boolean>(
    () => this.selectedFile() !== null && this.confirmationText().trim().toUpperCase() === 'IMPORT',
  );

  triggerFilePicker(): void {
    if (this.isImporting()) {
      return;
    }
    this.fileInput()?.nativeElement.click();
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement | null;
    const file = input?.files?.[0] ?? null;
    this.applySelectedFile(file);
  }

  formatFileSize(bytes: number): string {
    if (bytes < 1024) {
      return `${bytes} B`;
    }
    const kb = bytes / 1024;
    if (kb < 1024) {
      return `${kb.toFixed(1)} KB`;
    }
    const mb = kb / 1024;
    if (mb < 1024) {
      return `${mb.toFixed(1)} MB`;
    }
    return `${(mb / 1024).toFixed(2)} GB`;
  }

  confirmImport(): void {
    const file = this.selectedFile();
    if (!file || !this.isConfirmed() || this.isImporting()) {
      return;
    }

    this.isImporting.set(true);
    this.errorMessage.set(null);

    this.accountService.importBackup(file).subscribe({
      next: (result) => {
        this.isImporting.set(false);
        this.dialogRef.close({ success: true, result });
      },
      error: (err: { status?: number; error?: { detail?: string; title?: string } | string }) => {
        this.isImporting.set(false);
        this.errorMessage.set(this.formatImportError(err));
      },
    });
  }

  close(): void {
    if (!this.isImporting()) {
      this.dialogRef.close({ success: false });
    }
  }

  private formatImportError(err: {
    status?: number;
    error?: { detail?: string; title?: string } | string;
  }): string {
    const body = err?.error;
    if (body && typeof body === 'object') {
      if (body.detail || body.title) {
        return body.detail || body.title || 'Failed to restore backup archive.';
      }
    }

    switch (err?.status) {
      case 404:
        return 'Backup import endpoint was not found (HTTP 404). Restart the backend API server so it runs the latest build.';
      case 413:
        return 'Backup archive exceeds the maximum upload size allowed by the server (HTTP 413).';
      case 0:
      case 502:
      case 504:
        return 'Connection to the backend API server failed or timed out while uploading the backup archive.';
      default:
        if (typeof err?.status === 'number' && err.status > 0) {
          return `Backup restore failed (HTTP ${err.status}). Please check the server logs and try again.`;
        }
        return 'Failed to restore backup archive. Please verify the file is a valid LocalRoast .zip backup.';
    }
  }

  private applySelectedFile(file: File | null): void {
    if (!file) {
      this.selectedFile.set(null);
      return;
    }

    if (!file.name.toLowerCase().endsWith('.zip')) {
      this.selectedFile.set(null);
      this.errorMessage.set('Please select a valid .zip backup archive exported by LocalRoast.');
      return;
    }

    if (file.size <= 0) {
      this.selectedFile.set(null);
      this.errorMessage.set('The selected backup archive is empty.');
      return;
    }

    this.errorMessage.set(null);
    this.selectedFile.set(file);
  }
}
