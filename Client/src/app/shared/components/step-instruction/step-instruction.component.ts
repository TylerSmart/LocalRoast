import { Component, computed, input } from '@angular/core';
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

  protected readonly segments = computed(() =>
    buildInstructionSegments(this.step(), this.ingredients(), this.factor(), this.unitSystem()),
  );
}
