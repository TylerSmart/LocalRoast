import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { ResetAppDataResultDto } from '../../../core/models/account.models';
import { AccountService } from '../../../core/services/account/account.service';
import { ResetAppDataDialogComponent } from './reset-app-data-dialog.component';

describe('ResetAppDataDialogComponent', () => {
  let component: ResetAppDataDialogComponent;
  let fixture: ComponentFixture<ResetAppDataDialogComponent>;

  let mockAccountService: {
    resetAppData: ReturnType<typeof vi.fn>;
  };

  let mockDialogRef: {
    close: ReturnType<typeof vi.fn>;
  };

  const sampleResult: ResetAppDataResultDto = {
    deletedRecipesCount: 42,
    deletedMealPlansCount: 2,
    deletedShoppingListsCount: 3,
    deletedIngestionJobsCount: 15,
    deletedFoodItemsCount: 88,
    deletedTagsCount: 12,
    deletedCollectionsCount: 4,
    resetAt: '2026-11-04T12:00:00Z',
  };

  beforeEach(async () => {
    mockAccountService = {
      resetAppData: vi.fn(),
    };

    mockDialogRef = {
      close: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [ResetAppDataDialogComponent],
      providers: [
        { provide: AccountService, useValue: mockAccountService },
        { provide: MatDialogRef, useValue: mockDialogRef },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ResetAppDataDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes with empty confirmation and disabled reset button', () => {
    expect(component.confirmationText()).toBe('');
    expect(component.isConfirmed()).toBe(false);
    expect(component.isResetting()).toBe(false);
  });

  it('requires DELETE to confirm reset', () => {
    component.confirmationText.set('DEL');
    expect(component.isConfirmed()).toBe(false);

    component.confirmationText.set('DELETE');
    expect(component.isConfirmed()).toBe(true);

    component.confirmationText.set('delete');
    expect(component.isConfirmed()).toBe(true);
  });

  it('calls accountService.resetAppData and closes with success on confirmation', () => {
    mockAccountService.resetAppData.mockReturnValue(of(sampleResult));
    component.confirmationText.set('DELETE');

    component.confirmReset();

    expect(mockAccountService.resetAppData).toHaveBeenCalledTimes(1);
    expect(mockDialogRef.close).toHaveBeenCalledWith({
      success: true,
      result: sampleResult,
    });
  });

  it('sets error message when resetAppData fails', () => {
    mockAccountService.resetAppData.mockReturnValue(
      throwError(() => ({
        error: { detail: 'Database transaction failed.' },
      })),
    );
    component.confirmationText.set('DELETE');

    component.confirmReset();

    expect(component.isResetting()).toBe(false);
    expect(component.errorMessage()).toBe('Database transaction failed.');
    expect(mockDialogRef.close).not.toHaveBeenCalled();
  });

  it('closes with success: false when close is called', () => {
    component.close();
    expect(mockDialogRef.close).toHaveBeenCalledWith({ success: false });
  });

  it('does not close or confirm if currently resetting', () => {
    component.confirmationText.set('DELETE');
    component.isResetting.set(true);

    component.confirmReset();
    expect(mockAccountService.resetAppData).not.toHaveBeenCalled();

    component.close();
    expect(mockDialogRef.close).not.toHaveBeenCalled();
  });
});
