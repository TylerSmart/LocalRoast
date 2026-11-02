import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import {
  DuplicateMealPolicy,
  GenerateFromMealPlanRequest,
  ShoppingListGenerationPreviewDto,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  addDaysIso,
  getTodayIsoLocal,
  normalizeToMondayIso,
} from '../../../core/utils/meal-plan-date';

export interface GenerateShoppingListDialogData {
  targetShoppingListId?: string;
  startDate?: string;
  endDate?: string;
}

@Component({
  selector: 'app-generate-shopping-list-dialog',
  templateUrl: './generate-shopping-list-dialog.component.html',
  styleUrl: './generate-shopping-list-dialog.component.scss',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
    MatRadioModule,
    MatExpansionModule,
    MatProgressSpinnerModule,
    MatButtonToggleModule,
  ],
})
export class GenerateShoppingListDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly dialogRef = inject(MatDialogRef<GenerateShoppingListDialogComponent>);
  readonly data = inject<GenerateShoppingListDialogData>(MAT_DIALOG_DATA, { optional: true });
  private readonly shoppingService = inject(ShoppingService);

  readonly shoppingLists = signal<ShoppingListSummaryDto[]>([]);
  readonly preview = signal<ShoppingListGenerationPreviewDto | null>(null);
  readonly isLoadingLists = signal(true);
  readonly isPreviewing = signal(false);
  readonly isGenerating = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly promotedStaples = signal<Set<string>>(new Set());

  readonly form = this.fb.nonNullable.group({
    targetShoppingListId: ['', [Validators.required]],
    startDate: ['', [Validators.required]],
    endDate: ['', [Validators.required]],
    includeOnDeck: [true],
    includeSubRecipes: [true],
    includeOptionalIngredients: [true],
    duplicatePolicy: ['SkipAlreadyImported' as DuplicateMealPolicy],
  });

  ngOnInit(): void {
    const today = getTodayIsoLocal();
    const monday = this.data?.startDate || normalizeToMondayIso(today);
    const sunday = this.data?.endDate || addDaysIso(monday, 6);

    this.form.patchValue({
      startDate: monday,
      endDate: sunday,
    });

    this.loadLists();

    this.form.valueChanges.subscribe(() => {
      this.refreshPreview();
    });
  }

  private loadLists(): void {
    this.isLoadingLists.set(true);
    this.shoppingService.getShoppingLists().subscribe({
      next: (lists) => {
        this.shoppingLists.set(lists);
        this.isLoadingLists.set(false);

        let initialListId = this.data?.targetShoppingListId;
        if (!initialListId && lists.length > 0) {
          const pinned = lists.find((l) => l.isPinned);
          initialListId = pinned ? pinned.id : lists[0].id;
        }

        if (initialListId) {
          this.form.patchValue({ targetShoppingListId: initialListId }, { emitEvent: false });
          this.refreshPreview();
        }
      },
      error: () => {
        this.isLoadingLists.set(false);
        this.errorMessage.set('Failed to load shopping lists.');
      },
    });
  }

  refreshPreview(): void {
    const val = this.form.getRawValue();
    if (!val.targetShoppingListId || !val.startDate || !val.endDate) {
      return;
    }

    this.isPreviewing.set(true);
    this.errorMessage.set(null);

    const request: GenerateFromMealPlanRequest = {
      startDate: val.startDate,
      endDate: val.endDate,
      includeOnDeck: val.includeOnDeck ?? true,
      includeSubRecipes: val.includeSubRecipes ?? true,
      includeOptionalIngredients: val.includeOptionalIngredients ?? true,
      duplicatePolicy: val.duplicatePolicy ?? 'SkipAlreadyImported',
      promotePantryFoodItemIds: Array.from(this.promotedStaples()),
    };

    this.shoppingService.previewGenerateFromMealPlan(val.targetShoppingListId, request).subscribe({
      next: (res) => {
        this.preview.set(res);
        this.isPreviewing.set(false);
      },
      error: (err) => {
        this.isPreviewing.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to calculate preview.',
        );
      },
    });
  }

  togglePromoteStaple(foodItemId: string | null | undefined): void {
    if (!foodItemId) return;

    this.promotedStaples.update((set) => {
      const next = new Set(set);
      if (next.has(foodItemId)) {
        next.delete(foodItemId);
      } else {
        next.add(foodItemId);
      }
      return next;
    });

    this.refreshPreview();
  }

  onSubmit(): void {
    if (this.form.invalid || this.isGenerating()) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    this.isGenerating.set(true);
    this.errorMessage.set(null);

    const request: GenerateFromMealPlanRequest = {
      startDate: val.startDate,
      endDate: val.endDate,
      includeOnDeck: val.includeOnDeck ?? true,
      includeSubRecipes: val.includeSubRecipes ?? true,
      includeOptionalIngredients: val.includeOptionalIngredients ?? true,
      duplicatePolicy: val.duplicatePolicy ?? 'SkipAlreadyImported',
      promotePantryFoodItemIds: Array.from(this.promotedStaples()),
    };

    this.shoppingService.generateFromMealPlan(val.targetShoppingListId, request).subscribe({
      next: (result) => {
        this.dialogRef.close(result);
      },
      error: (err) => {
        this.isGenerating.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to generate shopping list items.',
        );
      },
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
