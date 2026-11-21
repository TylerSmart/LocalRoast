import { Component, computed, input, output } from '@angular/core';
import { UnitSystem } from '../../../core/models/recipe.models';
import {
  InstructionIngredient,
  InstructionStep,
  buildInstructionSegments,
} from '../../../core/utils/step-instruction';

@Component({
  selector: 'app-step-instruction',
  templateUrl: './step-instruction.component.html',
  styleUrl: './step-instruction.component.scss',
})
export class StepInstructionComponent {
  readonly step = input.required<InstructionStep>();
  readonly ingredients = input<readonly InstructionIngredient[]>([]);
  readonly factor = input<number>(1);
  readonly unitSystem = input<UnitSystem>('Original');
  readonly highlightedIngredientId = input<string | null>(null);

  readonly ingredientHover = output<string | null>();
  readonly ingredientSelect = output<string>();

  protected readonly segments = computed(() =>
    buildInstructionSegments(this.step(), this.ingredients(), this.factor(), this.unitSystem()),
  );

  protected onIngredientClick(event: MouseEvent, id: string | null | undefined): void {
    if (!id) return;
    event.stopPropagation();
    this.ingredientSelect.emit(id);
  }

  protected onIngredientKeydown(event: Event, id: string | null | undefined): void {
    if (!id) return;
    event.preventDefault();
    event.stopPropagation();
    this.ingredientSelect.emit(id);
  }
}
