import { Component, computed, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { UnitSystem } from '../../../core/models/recipe.models';
import { StepIngredientToken } from '../../../core/models/step-token.models';
import { formatTokenDisplay } from '../../../core/utils/step-token-parser';

@Component({
  selector: 'app-step-ingredient-chip',
  imports: [CommonModule, MatChipsModule, MatIconModule, MatTooltipModule],
  templateUrl: './step-ingredient-chip.component.html',
  styleUrl: './step-ingredient-chip.component.scss',
})
export class StepIngredientChipComponent {
  readonly token = input.required<StepIngredientToken>();
  readonly factor = input<number>(1);
  readonly unitSystem = input<UnitSystem>('Original');
  readonly editable = input<boolean>(false);

  readonly edit = output<StepIngredientToken>();

  protected readonly displayText = computed(() => {
    return formatTokenDisplay(this.token(), this.factor(), this.unitSystem());
  });

  protected readonly isScaled = computed(() => {
    return (
      (this.factor() !== 1 || this.unitSystem() !== 'Original') &&
      (this.token().displayAmount ?? this.token().amount) != null
    );
  });

  protected readonly tooltipText = computed(() => {
    const t = this.token();
    const parts: string[] = [];

    if (t.displayAmount != null && t.amount != null) {
      const totalDisplay = formatTokenDisplay(
        { ...t, displayAmount: t.amount },
        this.factor(),
        this.unitSystem(),
      );
      parts.push(`Total recipe uses: ${totalDisplay}`);
    }

    if (t.notes) {
      parts.push(`Note: ${t.notes}`);
    }

    if (this.editable()) {
      parts.push('(Click to edit)');
    }

    return parts.join(' • ');
  });

  protected onClick(): void {
    if (this.editable()) {
      this.edit.emit(this.token());
    }
  }
}
