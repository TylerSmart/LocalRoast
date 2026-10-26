import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { CollectionDto } from '../../../core/models/taxonomy.models';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { CollectionDialogComponent, CollectionDialogData } from './collection-dialog.component';

describe('CollectionDialogComponent', () => {
  let component: CollectionDialogComponent;
  let fixture: ComponentFixture<CollectionDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  const mockTaxonomyService = {
    createCollection: vi.fn(),
    updateCollection: vi.fn(),
  };

  const dialogData: CollectionDialogData = {
    initialName: 'Desserts',
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [CollectionDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CollectionDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('prefills initial name from data', () => {
    expect(component).toBeTruthy();
    expect(component['form'].controls.name.value).toBe('Desserts');
  });

  it('creates collection and closes dialog with saved entity', () => {
    const saved: CollectionDto = {
      id: 'c-new',
      name: 'Desserts',
      slug: 'desserts',
      description: 'Sweet treats',
      sortOrder: 1,
      isSystemSeeded: false,
      recipeCount: 0,
    };
    mockTaxonomyService.createCollection.mockReturnValue(of(saved));

    component['form'].controls.description.setValue('Sweet treats');
    component['form'].controls.sortOrder.setValue(1);
    component.onSave();

    expect(mockTaxonomyService.createCollection).toHaveBeenCalledWith({
      name: 'Desserts',
      description: 'Sweet treats',
      sortOrder: 1,
    });
    expect(mockDialogRef.close).toHaveBeenCalledWith(saved);
  });
});
