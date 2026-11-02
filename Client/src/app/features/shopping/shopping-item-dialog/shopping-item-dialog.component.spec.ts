import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { of } from 'rxjs';
import { ShoppingListItemDto } from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  ShoppingItemDialogComponent,
  ShoppingItemDialogData,
} from './shopping-item-dialog.component';

describe('ShoppingItemDialogComponent', () => {
  let component: ShoppingItemDialogComponent;
  let fixture: ComponentFixture<ShoppingItemDialogComponent>;

  const mockShoppingService = {
    addItem: vi.fn(),
    updateItem: vi.fn(),
  };

  const mockDialogRef = {
    close: vi.fn(),
  };

  const dialogData: ShoppingItemDialogData = {
    listId: 'list-1',
  };

  beforeEach(async () => {
    mockShoppingService.addItem.mockReset();
    mockShoppingService.updateItem.mockReset();
    mockDialogRef.close.mockReset();

    await TestBed.configureTestingModule({
      imports: [ShoppingItemDialogComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: ShoppingService, useValue: mockShoppingService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ShoppingItemDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('creates component and validates required name', () => {
    expect(component).toBeTruthy();
    expect(component.form.valid).toBe(false);

    component.form.controls.displayName.setValue('Apples');
    expect(component.form.valid).toBe(true);
  });

  it('submits create request and closes dialog on success', () => {
    const mockCreated: ShoppingListItemDto = {
      id: 'item-1',
      shoppingListId: 'list-1',
      displayName: 'Apples',
      quantity: 5,
      unitLabel: '',
      formattedAmount: '5',
      dimension: 'Discrete',
      isPantryStapleExcluded: false,
      isChecked: false,
      sortOrder: 0,
      sources: [],
      createdAt: '2026-11-02T00:00:00Z',
      modifiedAt: '2026-11-02T00:00:00Z',
    };

    mockShoppingService.addItem.mockReturnValue(of(mockCreated));

    component.form.controls.displayName.setValue('Apples');
    component.form.controls.quantity.setValue(5);
    component.onSubmit();

    expect(mockShoppingService.addItem).toHaveBeenCalled();
    expect(mockDialogRef.close).toHaveBeenCalledWith(mockCreated);
  });
});
