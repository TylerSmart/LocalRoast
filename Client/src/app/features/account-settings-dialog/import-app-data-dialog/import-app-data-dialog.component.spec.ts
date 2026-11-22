import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { of, throwError } from 'rxjs';
import { ImportAppDataBackupResultDto } from '../../../core/models/account.models';
import { AccountService } from '../../../core/services/account/account.service';
import { ImportAppDataDialogComponent } from './import-app-data-dialog.component';

describe('ImportAppDataDialogComponent', () => {
  let component: ImportAppDataDialogComponent;
  let fixture: ComponentFixture<ImportAppDataDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let mockAccountService: { importBackup: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };
    mockAccountService = { importBackup: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [ImportAppDataDialogComponent],
      providers: [
        provideAnimationsAsync('noop'),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: AccountService, useValue: mockAccountService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ImportAppDataDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('requires both a .zip file and IMPORT confirmation text before enabling restore', () => {
    expect(component.isConfirmed()).toBe(false);

    component.confirmationText.set('IMPORT');
    expect(component.isConfirmed()).toBe(false);

    const file = new File(['zip-bytes'], 'backup.zip', { type: 'application/zip' });
    component.onFileSelected({
      target: { files: [file] },
    } as unknown as Event);

    expect(component.selectedFile()).toBe(file);
    expect(component.isConfirmed()).toBe(true);
  });

  it('rejects non-zip files with a validation error', () => {
    const file = new File(['plain-text'], 'notes.txt', { type: 'text/plain' });
    component.onFileSelected({
      target: { files: [file] },
    } as unknown as Event);

    expect(component.selectedFile()).toBeNull();
    expect(component.errorMessage()).toContain('.zip');
  });

  it('submits backup archive and closes dialog with import result on success', () => {
    const expectedResult: ImportAppDataBackupResultDto = {
      importedRecipesCount: 14,
      importedFoodItemsCount: 30,
      importedTagsCount: 20,
      importedCollectionsCount: 2,
      importedMealPlansCount: 4,
      importedShoppingListsCount: 1,
      importedIngestionJobsCount: 3,
      importedCookLogsCount: 2,
      importedMediaFilesCount: 8,
      importedAt: '2026-11-22T20:00:00Z',
    };
    mockAccountService.importBackup.mockReturnValue(of(expectedResult));

    const file = new File(['zip-bytes'], 'backup.zip', { type: 'application/zip' });
    component.onFileSelected({
      target: { files: [file] },
    } as unknown as Event);
    component.confirmationText.set('import');

    component.confirmImport();

    expect(mockAccountService.importBackup).toHaveBeenCalledWith(file);
    expect(mockDialogRef.close).toHaveBeenCalledWith({
      success: true,
      result: expectedResult,
    });
  });

  it('shows server ProblemDetails detail message when import fails', () => {
    mockAccountService.importBackup.mockReturnValue(
      throwError(() => ({
        error: { detail: 'Unsupported backup archive format version 99.' },
      })),
    );

    const file = new File(['zip-bytes'], 'backup.zip', { type: 'application/zip' });
    component.onFileSelected({
      target: { files: [file] },
    } as unknown as Event);
    component.confirmationText.set('IMPORT');

    component.confirmImport();

    expect(component.isImporting()).toBe(false);
    expect(component.errorMessage()).toBe('Unsupported backup archive format version 99.');
    expect(mockDialogRef.close).not.toHaveBeenCalled();
  });

  it('shows actionable message when backend returns HTTP 404 (stale server build)', () => {
    mockAccountService.importBackup.mockReturnValue(
      throwError(() => ({
        status: 404,
      })),
    );

    const file = new File(['zip-bytes'], 'backup.zip', { type: 'application/zip' });
    component.onFileSelected({
      target: { files: [file] },
    } as unknown as Event);
    component.confirmationText.set('IMPORT');

    component.confirmImport();

    expect(component.isImporting()).toBe(false);
    expect(component.errorMessage()).toContain('HTTP 404');
    expect(component.errorMessage()).toContain('Restart the backend API server');
    expect(mockDialogRef.close).not.toHaveBeenCalled();
  });
});
