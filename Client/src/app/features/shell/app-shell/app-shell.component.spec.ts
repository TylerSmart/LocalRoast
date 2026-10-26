import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { APP_NAME } from '../../../app.branding';
import { AccountService } from '../../../core/services/account/account.service';
import { AuthService } from '../../../core/services/auth/auth.service';
import { ThemeService } from '../../../core/services/theme/theme.service';
import { AppShellComponent } from './app-shell.component';

describe('AppShellComponent', () => {
  let component: AppShellComponent;
  let fixture: ComponentFixture<AppShellComponent>;
  let mockDialog: { open: ReturnType<typeof vi.fn>; openDialogs: unknown[] };

  const mockAuthService = {
    currentUser: () => ({ id: '1', username: 'chef', email: 'chef@example.com' }),
    logout: vi.fn(),
  };

  const mockAccountService = {
    openAccountSettingsDialog: vi.fn(),
  };

  const mockThemeService = {
    mode: () => 'light',
    setMode: vi.fn(),
    openThemeSettingsDialog: vi.fn(),
  };

  beforeEach(async () => {
    mockDialog = {
      open: vi.fn(),
      openDialogs: [],
    };

    await TestBed.configureTestingModule({
      imports: [AppShellComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: mockAuthService },
        { provide: AccountService, useValue: mockAccountService },
        { provide: ThemeService, useValue: mockThemeService },
        { provide: MatDialog, useValue: mockDialog },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AppShellComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders branding and navigation links', () => {
    expect(component).toBeTruthy();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain(APP_NAME);
    expect(text).toContain('Cookbook');
    expect(text).toContain('Matcher');
    expect(text).toContain('Food Catalog');
  });

  it('opens search dialog on openGlobalSearch()', () => {
    component.openGlobalSearch();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('handles Ctrl+K shortcut to open search', () => {
    const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true });
    vi.spyOn(event, 'preventDefault');

    component.onGlobalKeydown(event);
    expect(event.preventDefault).toHaveBeenCalled();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('calls authService.logout on onLogout', () => {
    component.onLogout();
    expect(mockAuthService.logout).toHaveBeenCalled();
  });
});
