import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import type {
  IngestionDraftIngredientDto,
  IngestionDraftStepDto,
  IngestionExtractedDraftDto,
  IngestionRepairPreviewDto,
} from '../../../core/models/ingestion.models';
import type { RecipeMeasurement } from '../../../core/models/recipe.models';

export interface IngestionRepairDialogData {
  before: IngestionExtractedDraftDto;
  preview: IngestionRepairPreviewDto;
  collections: { id: string; name: string }[];
  tags: { id: string; name: string }[];
  title?: string;
}

interface RepairChange {
  section: string;
  field: string;
  before: string;
  after: string;
}

@Component({
  selector: 'app-ingestion-repair-dialog',
  imports: [MatButtonModule, MatDialogTitle, MatDialogContent, MatDialogActions],
  templateUrl: './ingestion-repair-dialog.component.html',
  styleUrl: './ingestion-repair-dialog.component.scss',
})
export class IngestionRepairDialogComponent {
  readonly data = inject<IngestionRepairDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<IngestionRepairDialogComponent, boolean>);
  readonly changes = this.compareDrafts(this.data.before, this.data.preview.draft);

  close(apply: boolean): void {
    this.dialogRef.close(apply);
  }

  private compareDrafts(
    before: IngestionExtractedDraftDto,
    after: IngestionExtractedDraftDto,
  ): RepairChange[] {
    const changes: RepairChange[] = [];
    const add = (section: string, field: string, oldValue: string, newValue: string) => {
      if (oldValue !== newValue) {
        changes.push({ section, field, before: oldValue, after: newValue });
      }
    };
    const draftFields: [keyof IngestionExtractedDraftDto, string][] = [
      ['title', 'Title'],
      ['description', 'Description'],
      ['baseServings', 'Base servings'],
      ['yieldDisplay', 'Yield'],
      ['prepTimeMinutes', 'Prep time (minutes)'],
      ['cookTimeMinutes', 'Cook time (minutes)'],
      ['totalTimeMinutes', 'Total time (minutes)'],
      ['difficulty', 'Difficulty'],
      ['sourceUrl', 'Source URL'],
      ['sourceName', 'Source name'],
      ['sourceAuthor', 'Source author'],
      ['sourceBookTitle', 'Source book'],
      ['sourceBookPage', 'Source page'],
      ['coverImagePath', 'Cover image'],
      ['thumbnailImagePath', 'Thumbnail'],
      ['retainSourceAttachments', 'Retain source attachments'],
      ['proposedTagNames', 'New tags'],
      ['measurementWarnings', 'Measurement warnings'],
      ['confidenceScore', 'Confidence'],
      ['extractedModel', 'Extraction model'],
    ];
    for (const [key, label] of draftFields) {
      add('Recipe', label, this.format(before[key]), this.format(after[key]));
    }
    add(
      'Recipe',
      'Collections',
      this.formatNames(before.suggestedCollectionIds, this.data.collections),
      this.formatNames(after.suggestedCollectionIds, this.data.collections),
    );
    add(
      'Recipe',
      'Tags',
      this.formatNames(before.suggestedTagIds, this.data.tags),
      this.formatNames(after.suggestedTagIds, this.data.tags),
    );

    const ingredientFields: [keyof IngestionDraftIngredientDto, string][] = [
      ['sectionName', 'Section'],
      ['originalRawText', 'Source line'],
      ['displayName', 'Name'],
      ['quantity', 'Quantity'],
      ['maxQuantity', 'Maximum quantity'],
      ['unit', 'Unit'],
      ['customUnitName', 'Custom unit'],
      ['preparationNote', 'Preparation'],
      ['isOptional', 'Optional'],
      ['matchedFoodItemId', 'Matched food ID'],
      ['matchedFoodItemName', 'Matched food'],
      ['isNewFoodItem', 'New food'],
      ['createNewFoodItem', 'Create food'],
      ['proposedFoodItemName', 'Proposed food name'],
      ['suggestedIsPantryStaple', 'Pantry staple'],
      ['alternativeMeasurement', 'Alternative measurement'],
      ['preferAlternativeMeasurement', 'Prefer alternative measurement'],
      ['suggestedCategory', 'Food category'],
      ['isRecipeReference', 'Recipe reference'],
      ['referencedRecipeTitle', 'Referenced recipe'],
    ];
    for (let i = 0; i < Math.max(before.ingredients.length, after.ingredients.length); i++) {
      const oldItem = before.ingredients[i];
      const newItem = after.ingredients[i];
      for (const [key, label] of ingredientFields) {
        const oldValue = oldItem?.[key];
        const newValue = newItem?.[key];
        add(
          `Ingredient ${i + 1}`,
          label,
          key === 'alternativeMeasurement'
            ? this.formatMeasurement(oldItem?.alternativeMeasurement)
            : this.format(oldValue),
          key === 'alternativeMeasurement'
            ? this.formatMeasurement(newItem?.alternativeMeasurement)
            : this.format(newValue),
        );
      }
    }

    for (let i = 0; i < Math.max(before.steps.length, after.steps.length); i++) {
      const oldStep = before.steps[i];
      const newStep = after.steps[i];
      const section = `Step ${i + 1}`;
      const fields: [keyof IngestionDraftStepDto, string][] = [
        ['sectionName', 'Section'],
        ['instruction', 'Instruction'],
        ['imagePath', 'Image'],
      ];
      for (const [key, label] of fields) {
        add(section, label, this.format(oldStep?.[key]), this.format(newStep?.[key]));
      }
      add(
        section,
        'Linked ingredients',
        this.formatLinks(oldStep, before),
        this.formatLinks(newStep, after),
      );
      add(
        section,
        'Ingredient portions',
        this.formatAmounts(oldStep, before),
        this.formatAmounts(newStep, after),
      );
      add(
        section,
        'Tagged amounts',
        this.formatInlineAmounts(oldStep, before),
        this.formatInlineAmounts(newStep, after),
      );
      add(
        section,
        'Timers',
        this.format(oldStep?.timers?.map((timer) => `${timer.label}: ${timer.durationSeconds}s`)),
        this.format(newStep?.timers?.map((timer) => `${timer.label}: ${timer.durationSeconds}s`)),
      );
    }
    return changes;
  }

  private format(value: unknown): string {
    if (value === null || value === undefined || value === '') return 'Not set';
    if (typeof value === 'boolean') return value ? 'Yes' : 'No';
    if (Array.isArray(value)) return value.length ? value.join('\n') : 'None';
    return String(value);
  }

  private formatNames(ids: string[] | null, options: { id: string; name: string }[]): string {
    return this.format(ids?.map((id) => options.find((option) => option.id === id)?.name ?? id));
  }

  private formatMeasurement(measurement: RecipeMeasurement | null | undefined): string {
    if (!measurement) return 'Not set';
    const quantity = measurement.quantity ?? '?';
    const range = measurement.maxQuantity == null ? '' : `-${measurement.maxQuantity}`;
    const unit = measurement.unit === 'Custom' ? measurement.customUnitName : measurement.unit;
    return `${quantity}${range} ${unit ?? ''}`.trim();
  }

  private formatInlineAmounts(
    step: IngestionDraftStepDto | undefined,
    draft: IngestionExtractedDraftDto,
  ): string {
    return this.format(
      step?.inlineAmounts?.map((amount) => {
        const text = step.instruction.slice(amount.start, amount.start + amount.length);
        const target =
          amount.kind === 'Ingredient' && amount.ingredientIndex !== null
            ? this.ingredientLabel(amount.ingredientIndex, draft)
            : amount.kind === 'Mixture'
              ? 'mixture'
              : 'other';
        return `"${text}" → ${target}`;
      }),
    );
  }

  private ingredientLabel(index: number, draft: IngestionExtractedDraftDto): string {
    return `${index + 1}. ${draft.ingredients[index]?.displayName ?? 'Unknown ingredient'}`;
  }

  private formatLinks(
    step: IngestionDraftStepDto | undefined,
    draft: IngestionExtractedDraftDto,
  ): string {
    return this.format(step?.ingredientIndices?.map((index) => this.ingredientLabel(index, draft)));
  }

  private formatAmounts(
    step: IngestionDraftStepDto | undefined,
    draft: IngestionExtractedDraftDto,
  ): string {
    return this.format(
      step?.ingredientAmounts?.map(
        (amount) =>
          `${this.ingredientLabel(amount.ingredientIndex, draft)}: ${this.formatMeasurement(amount.measurement)}`,
      ),
    );
  }
}
