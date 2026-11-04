import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MediaService } from '../../../core/services/media/media.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { CookLogDialogComponent, CookLogDialogData } from './cook-log-dialog.component';

describe('CookLogDialogComponent', () => {
  let component: CookLogDialogComponent;
  let fixture: ComponentFixture<CookLogDialogComponent>;
  let mockRecipeService: {
    createCookLog: ReturnType<typeof vi.fn>;
    updateCookLog: ReturnType<typeof vi.fn>;
  };
  let mockMediaService: {
    uploadCookLogPhoto: ReturnType<typeof vi.fn>;
  };
  let mockDialogRef: {
    close: ReturnType<typeof vi.fn>;
  };
  let mockSnackBar: {
    open: ReturnType<typeof vi.fn>;
  };

  const defaultData: CookLogDialogData = {
    recipeId: 'rec-123',
    recipeTitle: 'Pasta Carbonara',
    targetServings: 4,
  };

  beforeEach(async () => {
    mockRecipeService = {
      createCookLog: vi.fn().mockReturnValue(
        of({
          cookLog: { id: 'cl-1', recipeId: 'rec-123', rating: 5 },
          recipeStats: { recipeId: 'rec-123', timesCooked: 1 },
        }),
      ),
      updateCookLog: vi.fn().mockReturnValue(
        of({
          cookLog: { id: 'cl-1', recipeId: 'rec-123', rating: 4 },
          recipeStats: { recipeId: 'rec-123', timesCooked: 1 },
        }),
      ),
    };

    mockMediaService = {
      uploadCookLogPhoto: vi.fn(),
    };

    mockDialogRef = {
      close: vi.fn(),
    };

    mockSnackBar = {
      open: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [CookLogDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: defaultData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: MediaService, useValue: mockMediaService },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CookLogDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes with default servings and today date', () => {
    expect(component['form'].controls.servingsMade.value).toBe(4);
    expect(component['selectedRating']()).toBeNull();
  });

  it('selects and toggles rating stars', () => {
    component['setRating'](5);
    fixture.detectChanges();
    expect(component['selectedRating']()).toBe(5);
    const filledIcons = fixture.nativeElement.querySelectorAll('.star-icon.icon-filled');
    expect(filledIcons.length).toBe(5);

    // Clicking same rating clears it
    component['setRating'](5);
    fixture.detectChanges();
    expect(component['selectedRating']()).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.star-icon.icon-filled').length).toBe(0);

    component['setRating'](4);
    fixture.detectChanges();
    expect(component['selectedRating']()).toBe(4);
    expect(fixture.nativeElement.querySelectorAll('.star-icon.icon-filled').length).toBe(4);
    component['clearRating']();
    expect(component['selectedRating']()).toBeNull();
  });

  it('submits a new cook log and closes dialog with result', () => {
    component['setRating'](5);
    component['form'].patchValue({
      notes: 'Came out crisp and delicious!',
    });

    component['onSubmit']();

    expect(mockRecipeService.createCookLog).toHaveBeenCalledWith(
      'rec-123',
      expect.objectContaining({
        rating: 5,
        servingsMade: 4,
        notes: 'Came out crisp and delicious!',
      }),
    );
    expect(mockDialogRef.close).toHaveBeenCalled();
  });
});
