import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { TagDto } from '../../../core/models/taxonomy.models';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { TagDialogComponent, TagDialogData } from './tag-dialog.component';

describe('TagDialogComponent', () => {
  let component: TagDialogComponent;
  let fixture: ComponentFixture<TagDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  const mockTaxonomyService = {
    createTag: vi.fn(),
    updateTag: vi.fn(),
  };

  const dialogData: TagDialogData = {
    initialName: 'Sheet Pan',
    defaultCategory: 'Equipment',
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [TagDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TagDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('prefills initial values from data', () => {
    expect(component).toBeTruthy();
    expect(component['form'].controls.name.value).toBe('Sheet Pan');
    expect(component['form'].controls.category.value).toBe('Equipment');
  });

  it('creates tag and closes dialog with saved entity', () => {
    const saved: TagDto = {
      id: 't-new',
      name: 'Sheet Pan',
      slug: 'equipment-sheet-pan',
      category: 'Equipment',
      sortOrder: 5,
      isSystemSeeded: false,
      recipeCount: 0,
    };
    mockTaxonomyService.createTag.mockReturnValue(of(saved));

    component['form'].controls.sortOrder.setValue(5);
    component.onSave();

    expect(mockTaxonomyService.createTag).toHaveBeenCalledWith({
      name: 'Sheet Pan',
      category: 'Equipment',
      sortOrder: 5,
    });
    expect(mockDialogRef.close).toHaveBeenCalledWith(saved);
  });
});
