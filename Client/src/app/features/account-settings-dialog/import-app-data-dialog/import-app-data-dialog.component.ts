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
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isImporting.set(false);
        this.errorMessage.set(
          err?.error?.detail ||
            err?.error?.title ||
            'Failed to restore backup archive. Please verify the file is a valid LocalRoast .zip backup.',
        );
      },
    });
  }

  close(): void {
    if (!this.isImporting()) {
      this.dialogRef.close({ success: false });
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
