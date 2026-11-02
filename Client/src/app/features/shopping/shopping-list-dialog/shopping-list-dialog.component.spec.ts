import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { ShoppingListDialogComponent } from './shopping-list-dialog.component';

describe('ShoppingListDialogComponent', () => {
  let component: ShoppingListDialogComponent;
  let fixture: ComponentFixture<ShoppingListDialogComponent>;
  const mockDialogRef = { close: vi.fn() };

  beforeEach(async () => {
    mockDialogRef.close.mockReset();

    await TestBed.configureTestingModule({
      imports: [ShoppingListDialogComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: null },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ShoppingListDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create in creation mode', () => {
    expect(component).toBeTruthy();
    expect(component['isEdit']()).toBe(false);
  });

  it('should close dialog with trimmed result when valid', () => {
    component['form'].controls.name.setValue('  Costco Run  ');
    component['form'].controls.isPinned.setValue(true);

    component.onSubmit();

    expect(mockDialogRef.close).toHaveBeenCalledWith({
      name: 'Costco Run',
      isPinned: true,
    });
  });

  it('should not close dialog when form is invalid', () => {
    component['form'].controls.name.setValue('');
    component.onSubmit();
    expect(mockDialogRef.close).not.toHaveBeenCalled();
  });

  it('should close dialog without result on cancel', () => {
    component.onCancel();
    expect(mockDialogRef.close).toHaveBeenCalledWith();
  });
});
