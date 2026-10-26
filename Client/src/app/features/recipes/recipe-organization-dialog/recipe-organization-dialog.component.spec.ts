import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { CollectionDto, TagDto } from '../../../core/models/taxonomy.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import {
  RecipeOrganizationDialogComponent,
  RecipeOrganizationDialogData,
} from './recipe-organization-dialog.component';

describe('RecipeOrganizationDialogComponent', () => {
  let component: RecipeOrganizationDialogComponent;
  let fixture: ComponentFixture<RecipeOrganizationDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  const mockCollections: CollectionDto[] = [
    {
      id: 'c1',
      name: 'Weeknight Staples',
      slug: 'weeknight-staples',
      description: null,
      sortOrder: 1,
      isSystemSeeded: true,
      recipeCount: 2,
    },
  ];

  const mockTags: TagDto[] = [
    {
      id: 't1',
      name: 'Quick',
      slug: 'custom-quick',
      category: 'Custom',
      sortOrder: 1,
      isSystemSeeded: false,
      recipeCount: 1,
    },
  ];

  const mockTaxonomyService = {
    getCollections: vi.fn().mockReturnValue(of(mockCollections)),
    getTags: vi.fn().mockReturnValue(of(mockTags)),
  };

  const mockRecipeService = {
    updateRecipeOrganization: vi.fn(),
  };

  const dialogData: RecipeOrganizationDialogData = {
    recipeId: 'r1',
    recipeTitle: 'Tomato Soup',
    initialCollectionIds: ['c1'],
    initialTagIds: [],
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [RecipeOrganizationDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
        { provide: RecipeService, useValue: mockRecipeService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeOrganizationDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes with selected collections', () => {
    expect(component).toBeTruthy();
    expect(component['selectedCollectionIds']().has('c1')).toBe(true);
    expect(component['selectedTagIds']().size).toBe(0);
  });

  it('toggles tag and saves updated recipe organization', () => {
    const updatedRecipe: Partial<RecipeDetailDto> = {
      id: 'r1',
      title: 'Tomato Soup',
      collections: [{ id: 'c1', name: 'Weeknight Staples', slug: 'weeknight-staples' }],
      tags: [{ id: 't1', name: 'Quick', slug: 'custom-quick', category: 'Custom' }],
    };
    mockRecipeService.updateRecipeOrganization.mockReturnValue(
      of(updatedRecipe as RecipeDetailDto),
    );

    component.toggleTag('t1');
    expect(component['selectedTagIds']().has('t1')).toBe(true);

    component.onSave();

    expect(mockRecipeService.updateRecipeOrganization).toHaveBeenCalledWith('r1', {
      collectionIds: ['c1'],
      tagIds: ['t1'],
    });
    expect(mockDialogRef.close).toHaveBeenCalledWith(updatedRecipe);
  });
});
