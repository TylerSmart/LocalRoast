import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
import {
  CreatedPersonalAccessTokenDto,
  PersonalAccessTokenDto,
} from '../../../core/models/personal-access-token.models';
import { PersonalAccessTokenService } from '../../../core/services/personal-access-token/personal-access-token.service';
import { PersonalAccessTokensPanelComponent } from './personal-access-tokens-panel.component';

describe('PersonalAccessTokensPanelComponent', () => {
  let component: PersonalAccessTokensPanelComponent;
  let fixture: ComponentFixture<PersonalAccessTokensPanelComponent>;
  let mockTokenService: {
    getTokens: ReturnType<typeof vi.fn>;
    createToken: ReturnType<typeof vi.fn>;
    revokeToken: ReturnType<typeof vi.fn>;
    deleteToken: ReturnType<typeof vi.fn>;
  };
  let mockSnackBar: {
    open: ReturnType<typeof vi.fn>;
  };

  const sampleToken: PersonalAccessTokenDto = {
    id: 'tok-123',
    name: 'Antigravity Test Token',
    tokenPrefix: 'lr_pat_abcdef12',
    createdAt: '2026-10-27T00:00:00Z',
    expiresAt: null,
    lastUsedAt: null,
    revokedAt: null,
    isRevoked: false,
    isExpired: false,
  };

  beforeEach(async () => {
    mockTokenService = {
      getTokens: vi.fn().mockReturnValue(of([sampleToken])),
      createToken: vi.fn(),
      revokeToken: vi.fn(),
      deleteToken: vi.fn(),
    };
    mockSnackBar = {
      open: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [PersonalAccessTokensPanelComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: PersonalAccessTokenService, useValue: mockTokenService },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(PersonalAccessTokensPanelComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('creates component and loads initial tokens', () => {
    expect(component).toBeTruthy();
    expect(mockTokenService.getTokens).toHaveBeenCalled();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.token-name')?.textContent).toContain('Antigravity Test Token');
  });

  it('renders MCP client setup card and endpoint URL', () => {
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.mcp-config-card')).toBeTruthy();
    expect(compiled.querySelector('.endpoint-url')?.textContent).toContain('/api/mcp');
  });

  it('creates a token and reveals one-time secret banner', () => {
    const created: CreatedPersonalAccessTokenDto = {
      token: {
        id: 'tok-456',
        name: 'New Bot',
        tokenPrefix: 'lr_pat_11223344',
        createdAt: '2026-10-27T01:00:00Z',
        expiresAt: '2026-11-27T01:00:00Z',
        lastUsedAt: null,
        revokedAt: null,
        isRevoked: false,
        isExpired: false,
      },
      plaintextToken: 'lr_pat_mockPlaintextToken1234567890abcdef',
    };
    mockTokenService.createToken.mockReturnValue(of(created));

    component.tokenForm.controls.name.setValue('New Bot');
    component.tokenForm.controls.expirationDays.setValue(30);
    component.createToken();
    fixture.detectChanges();

    expect(mockTokenService.createToken).toHaveBeenCalled();
    const compiled = fixture.nativeElement as HTMLElement;
    const secretCode = compiled.querySelector('.pat-secret-value');
    expect(secretCode?.textContent).toContain('lr_pat_mockPlaintextToken1234567890abcdef');
    expect(mockSnackBar.open).toHaveBeenCalledWith(
      expect.stringContaining('generated successfully'),
      'Close',
      expect.anything(),
    );
  });

  it('dismisses secret reveal banner', () => {
    const created: CreatedPersonalAccessTokenDto = {
      token: sampleToken,
      plaintextToken: 'lr_pat_secret',
    };
    mockTokenService.createToken.mockReturnValue(of(created));
    component.tokenForm.controls.name.setValue('Sample');
    component.createToken();
    fixture.detectChanges();

    let banner = fixture.nativeElement.querySelector('.secret-reveal-banner');
    expect(banner).toBeTruthy();

    component.dismissSecretBanner();
    fixture.detectChanges();

    banner = fixture.nativeElement.querySelector('.secret-reveal-banner');
    expect(banner).toBeNull();
  });

  it('revokes a token and updates its UI status badge', () => {
    const revoked: PersonalAccessTokenDto = {
      ...sampleToken,
      isRevoked: true,
      revokedAt: '2026-10-27T02:00:00Z',
    };
    mockTokenService.revokeToken.mockReturnValue(of(revoked));

    component.revokeToken(sampleToken);
    fixture.detectChanges();

    expect(mockTokenService.revokeToken).toHaveBeenCalledWith(sampleToken.id);
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.badge-revoked')?.textContent).toContain('Revoked');
  });

  it('deletes a token and removes it from inventory', () => {
    mockTokenService.deleteToken.mockReturnValue(of(undefined));

    component.deleteToken(sampleToken);
    fixture.detectChanges();

    expect(mockTokenService.deleteToken).toHaveBeenCalledWith(sampleToken.id);
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.empty-tokens-state')).toBeTruthy();
  });

  it('handles load error gracefully', () => {
    mockTokenService.getTokens.mockReturnValue(
      throwError(() => ({ error: { detail: 'Server Error' } })),
    );
    component.loadTokens();
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.alert-error')?.textContent).toContain('Server Error');
  });
});
