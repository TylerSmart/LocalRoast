import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { APP_BRAND_MARK_SRC, APP_NAME } from '../../../app.branding';

@Component({
  selector: 'app-pwa-install-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  templateUrl: './pwa-install-dialog.component.html',
  styleUrl: './pwa-install-dialog.component.scss',
})
export class PwaInstallDialogComponent {
  protected readonly appName = APP_NAME;
  protected readonly brandMarkSrc = APP_BRAND_MARK_SRC;
  private readonly dialogRef = inject(MatDialogRef<PwaInstallDialogComponent>);

  close(): void {
    this.dialogRef.close();
  }
}
