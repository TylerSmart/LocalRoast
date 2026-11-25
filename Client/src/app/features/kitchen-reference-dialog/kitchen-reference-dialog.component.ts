import { Clipboard } from '@angular/cdk/clipboard';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DimensionUnit } from '../../core/models/step-token.models';
import { KitchenReferenceService } from '../../core/services/kitchen-reference/kitchen-reference.service';
import { KitchenTimerService } from '../../core/services/kitchen-timer/kitchen-timer.service';
import { formatCountdown, formatDuration } from '../../core/utils/culinary-math';
import {
  calculatePanScaling,
  celsiusToFahrenheit,
  convertCulinaryMeasurement,
  convertLengthDimension,
  describeOvenTemperature,
  fahrenheitToCelsius,
  searchKitchenReference,
} from '../../core/utils/kitchen-converter';
import {
  BAKING_PANS,
  CLASSIC_CULINARY_RATIOS,
  CONVERTER_UNITS,
  COOKING_RATIOS_AND_TIMINGS,
  CULINARY_GLOSSARY,
  CULINARY_SUBSTITUTIONS,
  ConverterSubMode,
  ConverterUnit,
  FOOD_TEMPERATURES,
  GLOSSARY_CATEGORIES,
  GlossaryCategory,
  INGREDIENT_DENSITIES,
  KitchenReferenceTab,
  QUICK_EQUIVALENTS,
  SUBSTITUTION_CATEGORIES,
  SubstitutionCategory,
  TEMPERATURE_CATEGORIES,
  TemperatureCategory,
} from '../../core/utils/kitchen-reference-data';

interface TabConfig {
  id: KitchenReferenceTab;
  label: string;
  icon: string;
}

interface SubModeConfig {
  id: ConverterSubMode;
  label: string;
  icon: string;
}

@Component({
  selector: 'app-kitchen-reference-dialog',
  imports: [
    DecimalPipe,
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
  ],
  templateUrl: './kitchen-reference-dialog.component.html',
  styleUrl: './kitchen-reference-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KitchenReferenceDialogComponent {
  protected readonly referenceService = inject(KitchenReferenceService);
  protected readonly kitchenTimerService = inject(KitchenTimerService);
  private readonly clipboard = inject(Clipboard);
  private readonly dialogRef = inject(MatDialogRef<KitchenReferenceDialogComponent>);

  protected readonly tabs: readonly TabConfig[] = [
    { id: 'convert', label: 'Convert', icon: 'calculate' },
    { id: 'temps', label: 'Temps', icon: 'thermostat' },
    { id: 'glossary', label: 'Glossary', icon: 'import_contacts' },
    { id: 'swaps', label: 'Swaps', icon: 'swap_horiz' },
    { id: 'ratios', label: 'Ratios & Timers', icon: 'timer' },
  ];

  protected readonly converterSubModes: readonly SubModeConfig[] = [
    { id: 'volume-weight', label: 'Volume & Weight', icon: 'scale' },
    { id: 'temperature', label: 'Oven Temp', icon: 'device_thermostat' },
    { id: 'dimensions', label: 'Dimensions', icon: 'straighten' },
    { id: 'baking-pans', label: 'Baking Pans', icon: 'cake' },
  ];

  protected readonly amountPresets = [
    { label: '1/4', value: 0.25 },
    { label: '1/3', value: 1 / 3 },
    { label: '1/2', value: 0.5 },
    { label: '2/3', value: 2 / 3 },
    { label: '3/4', value: 0.75 },
    { label: '1', value: 1 },
    { label: '2', value: 2 },
  ];

  protected readonly ovenTempPresetsF = [275, 325, 350, 375, 400, 425, 450, 500];
  protected readonly timerQuickPresets = [1, 3, 5, 10, 15, 30];

  protected readonly converterUnits = CONVERTER_UNITS;
  protected readonly ingredientDensities = INGREDIENT_DENSITIES;
  protected readonly quickEquivalents = QUICK_EQUIVALENTS;
  protected readonly bakingPans = BAKING_PANS;

  protected readonly temperatureCategories = TEMPERATURE_CATEGORIES;
  protected readonly glossaryCategories = GLOSSARY_CATEGORIES;
  protected readonly substitutionCategories = SUBSTITUTION_CATEGORIES;
  protected readonly classicRatios = CLASSIC_CULINARY_RATIOS;

  protected readonly dimensionUnitOptions: readonly { value: DimensionUnit; label: string }[] = [
    { value: 'inch', label: 'Inches (in)' },
    { value: 'foot', label: 'Feet (ft)' },
    { value: 'mm', label: 'Millimeters (mm)' },
    { value: 'cm', label: 'Centimeters (cm)' },
    { value: 'm', label: 'Meters (m)' },
  ];

  // Converter state
  protected readonly converterSubMode = signal<ConverterSubMode>('volume-weight');
  protected readonly amount = signal<number>(1);
  protected readonly fromUnit = signal<ConverterUnit>('Cup');
  protected readonly toUnit = signal<ConverterUnit>('G');
  protected readonly selectedIngredientId = signal<string>('ap-flour');
  protected readonly copiedConversion = signal<boolean>(false);

  // Oven temp converter state
  protected readonly tempFahrenheit = signal<number>(350);
  protected readonly tempCelsius = signal<number>(fahrenheitToCelsius(350));

  // Dimension converter state
  protected readonly dimensionAmount = signal<number>(9);
  protected readonly dimensionUnit = signal<DimensionUnit>('inch');

  // Baking pan scaler state
  protected readonly fromPanId = signal<string>('round-8');
  protected readonly toPanId = signal<string>('round-9');

  // Reference filter states
  protected readonly selectedTempCategory = signal<TemperatureCategory | 'All'>('All');
  protected readonly selectedGlossaryCategory = signal<GlossaryCategory | 'All'>('All');
  protected readonly selectedSwapCategory = signal<SubstitutionCategory | 'All'>('All');

  // Standalone quick timer state
  protected readonly quickTimerLabel = signal<string>('Kitchen Timer');
  protected readonly quickTimerMinutes = signal<number>(5);

  protected readonly formatCountdown = formatCountdown;
  protected readonly formatDuration = formatDuration;
  protected readonly fahrenheitToCelsius = fahrenheitToCelsius;

  // Derived computations
  protected readonly activeTab = this.referenceService.activeTab;
  protected readonly searchQuery = this.referenceService.searchQuery;

  protected readonly hasSearchQuery = computed(() => this.searchQuery().trim().length > 0);

  protected readonly searchResults = computed(() => searchKitchenReference(this.searchQuery()));

  protected readonly conversionResult = computed(() =>
    convertCulinaryMeasurement(
      this.amount(),
      this.fromUnit(),
      this.toUnit(),
      this.selectedIngredientId(),
    ),
  );

  protected readonly ovenTempInfo = computed(() => describeOvenTemperature(this.tempFahrenheit()));

  protected readonly dimensionEquivalents = computed(() =>
    convertLengthDimension(this.dimensionAmount(), this.dimensionUnit()),
  );

  protected readonly panScalingResult = computed(() =>
    calculatePanScaling(this.fromPanId(), this.toPanId()),
  );

  protected readonly filteredTemperatures = computed(() => {
    const cat = this.selectedTempCategory();
    return cat === 'All' ? FOOD_TEMPERATURES : FOOD_TEMPERATURES.filter((t) => t.category === cat);
  });

  protected readonly sortedFilteredGlossary = computed(() => {
    const cat = this.selectedGlossaryCategory();
    const list =
      cat === 'All' ? CULINARY_GLOSSARY : CULINARY_GLOSSARY.filter((g) => g.category === cat);
    return [...list].sort((a, b) => a.term.localeCompare(b.term));
  });

  protected readonly filteredSubstitutions = computed(() => {
    const cat = this.selectedSwapCategory();
    return cat === 'All'
      ? CULINARY_SUBSTITUTIONS
      : CULINARY_SUBSTITUTIONS.filter((s) => s.category === cat);
  });

  protected readonly eggTimings = COOKING_RATIOS_AND_TIMINGS.filter((t) => t.group === 'Eggs');
  protected readonly grainTimings = COOKING_RATIOS_AND_TIMINGS.filter(
    (t) => t.group === 'Rice & Grains',
  );
  protected readonly legumeTimings = COOKING_RATIOS_AND_TIMINGS.filter(
    (t) => t.group === 'Oats & Legumes',
  );

  selectTab(tab: KitchenReferenceTab): void {
    this.referenceService.setActiveTab(tab);
  }

  onSearchInput(value: string): void {
    this.referenceService.setSearchQuery(value);
  }

  clearSearch(): void {
    this.referenceService.setSearchQuery('');
  }

  jumpToCalculator(subMode: ConverterSubMode): void {
    this.referenceService.setSearchQuery('');
    this.referenceService.setActiveTab('convert');
    this.converterSubMode.set(subMode);
  }

  onAmountChange(value: number | string | null): void {
    const parsed = typeof value === 'number' ? value : Number(value);
    this.amount.set(Number.isFinite(parsed) && parsed >= 0 ? parsed : 0);
  }

  setAmountPreset(value: number): void {
    this.amount.set(Number(value.toFixed(4)));
  }

  swapUnits(): void {
    const currentFrom = this.fromUnit();
    const currentTo = this.toUnit();
    this.fromUnit.set(currentTo);
    this.toUnit.set(currentFrom);
  }

  copyConversionResult(): void {
    const res = this.conversionResult();
    if (!res.formattedWithUnit) return;
    this.clipboard.copy(res.formattedWithUnit);
    this.copiedConversion.set(true);
    setTimeout(() => this.copiedConversion.set(false), 1600);
  }

  onFahrenheitChange(value: number | string | null): void {
    const parsed = typeof value === 'number' ? value : Number(value);
    if (!Number.isFinite(parsed)) return;
    this.tempFahrenheit.set(parsed);
    this.tempCelsius.set(fahrenheitToCelsius(parsed));
  }

  onCelsiusChange(value: number | string | null): void {
    const parsed = typeof value === 'number' ? value : Number(value);
    if (!Number.isFinite(parsed)) return;
    this.tempCelsius.set(parsed);
    this.tempFahrenheit.set(celsiusToFahrenheit(parsed));
  }

  onDimensionAmountChange(value: number | string | null): void {
    const parsed = typeof value === 'number' ? value : Number(value);
    this.dimensionAmount.set(Number.isFinite(parsed) && parsed >= 0 ? parsed : 0);
  }

  swapPans(): void {
    const from = this.fromPanId();
    const to = this.toPanId();
    this.fromPanId.set(to);
    this.toPanId.set(from);
  }

  startTimingPreset(label: string, seconds: number): void {
    this.kitchenTimerService.startTimer('', 'Kitchen Utility', label, seconds, null);
  }

  startQuickTimer(minutes?: number, customLabel?: string): void {
    const mins = minutes ?? Math.max(1, Math.min(720, Math.round(this.quickTimerMinutes() || 5)));
    const label = (customLabel ?? this.quickTimerLabel()).trim() || 'Kitchen Timer';
    this.kitchenTimerService.startTimer('', 'Kitchen Utility', label, mins * 60, null);
  }

  formatTempRangeC(tempF: number, maxTempF?: number): string {
    const minC = Math.round(fahrenheitToCelsius(tempF));
    if (maxTempF != null && maxTempF > tempF) {
      const maxC = Math.round(fahrenheitToCelsius(maxTempF));
      return `${minC}–${maxC}°C`;
    }
    return `${minC}°C`;
  }

  formatTempRangeF(tempF: number, maxTempF?: number): string {
    if (maxTempF != null && maxTempF > tempF) {
      return `${tempF}–${maxTempF}°F`;
    }
    return `${tempF}°F`;
  }

  close(): void {
    this.dialogRef.close();
  }
}
