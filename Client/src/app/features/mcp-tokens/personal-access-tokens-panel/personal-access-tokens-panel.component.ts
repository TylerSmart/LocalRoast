import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  CreatedPersonalAccessTokenDto,
  PersonalAccessTokenDto,
} from '../../../core/models/personal-access-token.models';
import { PersonalAccessTokenService } from '../../../core/services/personal-access-token/personal-access-token.service';

export interface ExpirationOption {
  label: string;
  days: number;
}

export const EXPIRATION_OPTIONS: ExpirationOption[] = [
  { label: 'Never expires', days: 0 },
  { label: '7 days', days: 7 },
  { label: '30 days', days: 30 },
  { label: '90 days', days: 90 },
  { label: '1 year (365 days)', days: 365 },
];

@Component({
  selector: 'app-personal-access-tokens-panel',
  imports: [
    DatePipe,
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './personal-access-tokens-panel.component.html',
  styleUrl: './personal-access-tokens-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PersonalAccessTokensPanelComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly tokenService = inject(PersonalAccessTokenService);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly expirationOptions = EXPIRATION_OPTIONS;

  protected readonly tokens = signal<PersonalAccessTokenDto[]>([]);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly loadError = signal<string | null>(null);

  protected readonly isCreating = signal<boolean>(false);
  protected readonly createError = signal<string | null>(null);
  protected readonly newlyCreatedToken = signal<CreatedPersonalAccessTokenDto | null>(null);

  protected readonly busyTokenId = signal<string | null>(null);

  protected readonly mcpEndpointUrl = computed<string>(() => {
    if (typeof window !== 'undefined' && window.location?.origin) {
      return `${window.location.origin}/api/mcp`;
    }
    return 'http://localhost:5041/api/mcp';
  });

  protected readonly mcpConfigSnippet = computed<string>(() => {
    const token = this.newlyCreatedToken()?.plaintextToken ?? '<YOUR_LR_PAT_TOKEN>';
    const config = {
      mcpServers: {
        localroast: {
          url: this.mcpEndpointUrl(),
          headers: {
            Authorization: `Bearer ${token}`,
          },
        },
      },
    };
    return JSON.stringify(config, null, 2);
  });

  readonly tokenForm = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(100)]],
    expirationDays: [0],
  });

  ngOnInit(): void {
    this.loadTokens();
  }

  loadTokens(): void {
    this.isLoading.set(true);
    this.loadError.set(null);

    this.tokenService.getTokens().subscribe({
      next: (tokens) => {
        this.tokens.set(tokens);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        this.loadError.set(
          err?.error?.detail || err?.error?.title || 'Failed to load personal access tokens.',
        );
      },
    });
  }

  createToken(): void {
    this.createError.set(null);

    if (this.tokenForm.invalid) {
      this.tokenForm.markAllAsTouched();
      return;
    }

    const { name, expirationDays } = this.tokenForm.getRawValue();
    const expiresAt =
      expirationDays > 0 ? new Date(Date.now() + expirationDays * 86_400_000).toISOString() : null;

    this.isCreating.set(true);
    this.tokenService.createToken({ name: name.trim(), expiresAt }).subscribe({
      next: (result) => {
        this.isCreating.set(false);
        this.newlyCreatedToken.set(result);
        this.tokens.update((list) => [
          result.token,
          ...list.filter((t) => t.id !== result.token.id),
        ]);
        this.tokenForm.reset({ name: '', expirationDays: 0 });
        this.snackBar.open('Personal access token generated successfully.', 'Close', {
          duration: 4000,
        });
      },
      error: (err) => {
        this.isCreating.set(false);
        this.createError.set(
          err?.error?.detail || err?.error?.title || 'Failed to create personal access token.',
        );
      },
    });
  }

  dismissSecretBanner(): void {
    this.newlyCreatedToken.set(null);
  }

  copyPlaintextToken(): void {
    const plaintext = this.newlyCreatedToken()?.plaintextToken;
    if (!plaintext) return;

    this.copyToClipboard(plaintext, 'Personal access token copied to clipboard!');
  }

  copyMcpConfig(): void {
    this.copyToClipboard(this.mcpConfigSnippet(), 'MCP configuration copied to clipboard!');
  }

  copyEndpointUrl(): void {
    this.copyToClipboard(this.mcpEndpointUrl(), 'MCP endpoint URL copied to clipboard!');
  }

  revokeToken(token: PersonalAccessTokenDto): void {
    this.busyTokenId.set(token.id);

    this.tokenService.revokeToken(token.id).subscribe({
      next: (updated) => {
        this.busyTokenId.set(null);
        this.tokens.update((list) => list.map((item) => (item.id === updated.id ? updated : item)));
        this.snackBar.open(`Token "${token.name}" revoked.`, 'Close', { duration: 3000 });
      },
      error: (err) => {
        this.busyTokenId.set(null);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to revoke token.',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  deleteToken(token: PersonalAccessTokenDto): void {
    this.busyTokenId.set(token.id);

    this.tokenService.deleteToken(token.id).subscribe({
      next: () => {
        this.busyTokenId.set(null);
        this.tokens.update((list) => list.filter((item) => item.id !== token.id));
        this.snackBar.open(`Token "${token.name}" deleted.`, 'Close', { duration: 3000 });
      },
      error: (err) => {
        this.busyTokenId.set(null);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to delete token.',
          'Close',
          { duration: 4000 },
        );
      },
    });
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
}
