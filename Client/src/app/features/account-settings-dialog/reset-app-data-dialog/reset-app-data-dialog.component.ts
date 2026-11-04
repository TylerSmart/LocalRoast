import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
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
import { ResetAppDataResultDto } from '../../../core/models/account.models';
import { AccountService } from '../../../core/services/account/account.service';

export interface ResetAppDataDialogResult {
  success: boolean;
  result?: ResetAppDataResultDto;
}

@Component({
  selector: 'app-reset-app-data-dialog',
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
  templateUrl: './reset-app-data-dialog.component.html',
  styleUrl: './reset-app-data-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetAppDataDialogComponent {
  private readonly dialogRef = inject(MatDialogRef<ResetAppDataDialogComponent>);
  private readonly accountService = inject(AccountService);

  readonly confirmationText = signal<string>('');
  readonly isResetting = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);

  readonly isConfirmed = computed<boolean>(
    () => this.confirmationText().trim().toUpperCase() === 'DELETE',
  );

  confirmReset(): void {
    if (!this.isConfirmed() || this.isResetting()) {
      return;
    }

    this.isResetting.set(true);
    this.errorMessage.set(null);

    this.accountService.resetAppData().subscribe({
      next: (result) => {
        this.isResetting.set(false);
        this.dialogRef.close({ success: true, result });
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isResetting.set(false);
        this.errorMessage.set(
          err?.error?.detail ||
            err?.error?.title ||
            'Failed to reset application data. Please try again.',
        );
      },
    });
  }

  close(): void {
    if (!this.isResetting()) {
      this.dialogRef.close({ success: false });
    }
  }
}
