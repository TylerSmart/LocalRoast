import {
  AfterViewInit,
  Component,
  ElementRef,
  computed,
  forwardRef,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import {
  StepDialogIngredientOption,
  StepIngredientToken,
  StepTemperatureToken,
  StepTimerToken,
  StepToken,
} from '../../../core/models/step-token.models';
import {
  formatTemperatureToken,
  formatTimerToken,
  formatTokenDisplay,
  insertTokenAtOffset,
  parseStepInstruction,
  replaceTokenSegmentAt,
} from '../../../core/utils/step-token-parser';
import {
  StepTokenDialogComponent,
  StepTokenDialogData,
  StepTokenDialogResult,
} from '../step-token-dialog/step-token-dialog.component';

@Component({
  selector: 'app-step-instruction-editor',
  imports: [MatButtonModule, MatIconModule],
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => StepInstructionEditorComponent),
      multi: true,
    },
  ],
  host: {
    '[attr.data-serialized-value]': 'serializedValue()',
    '[class.is-disabled]': 'disabled()',
    '[class.is-focused]': 'isFocused()',
  },
  templateUrl: './step-instruction-editor.component.html',
  styleUrl: './step-instruction-editor.component.scss',
})
export class StepInstructionEditorComponent implements ControlValueAccessor, AfterViewInit {
  private readonly dialog = inject(MatDialog);

  readonly label = input<string>('Step Instruction');
  readonly placeholder = input<string>('Describe this preparation or cooking step in detail...');
  readonly stepIndex = input<number>(0);
  readonly availableIngredients = input<readonly string[]>([]);
  readonly availableIngredientOptions = input<readonly StepDialogIngredientOption[]>([]);
  readonly required = input<boolean>(false);

  protected readonly editableSurface = viewChild<ElementRef<HTMLDivElement>>('editableSurface');

  protected readonly serializedValue = signal<string>('');
  protected readonly disabled = signal<boolean>(false);
  protected readonly isFocused = signal<boolean>(false);
  protected readonly isEmpty = computed(() => this.serializedValue().length === 0);

  private readonly mergedIngredientOptions = computed<StepDialogIngredientOption[]>(() => {
    const rich = this.availableIngredientOptions();
    if (rich.length > 0) {
      return [...rich];
    }
    return this.availableIngredients().map((displayName, index) => ({
      index,
      displayName,
    }));
  });

  private viewInitialized = false;
  private lastCaretOffset: number | null = null;
  private onChange: (value: string) => void = () => undefined;
  private onTouched: () => void = () => undefined;

  ngAfterViewInit(): void {
    this.viewInitialized = true;
    this.renderSegmentsToDom(this.serializedValue());
  }

  writeValue(value: string | null | undefined): void {
    const normalized = value ?? '';
    this.serializedValue.set(normalized);
    if (this.viewInitialized) {
      this.renderSegmentsToDom(normalized);
    }
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }

  protected captureCaretOffset(): void {
    const surface = this.editableSurface()?.nativeElement;
    if (!surface) return;
    const offset = this.getSelectionOffsetInsideSurface(surface);
    if (offset !== null) {
      this.lastCaretOffset = offset;
    }
  }

  protected onEditableFocus(): void {
    this.isFocused.set(true);
    this.captureCaretOffset();
  }

  protected onEditableBlur(): void {
    this.isFocused.set(false);
    this.onTouched();
  }

  protected onEditableInput(): void {
    const surface = this.editableSurface()?.nativeElement;
    if (!surface || this.disabled()) return;

    const raw = this.serializeDomToInstruction(surface);
    const needsUpgrade = this.hasRawTokenInTextNodes(surface);
    const caret = this.getSelectionOffsetInsideSurface(surface);
    if (caret !== null) {
      this.lastCaretOffset = caret;
    }

    this.serializedValue.set(raw);
    this.onChange(raw);

    if (needsUpgrade) {
      this.renderSegmentsToDom(raw);
      if (caret !== null) {
        this.restoreCaretAtSerializedOffset(caret);
      }
    }
  }

  protected onEditableKeyUp(): void {
    this.captureCaretOffset();
  }

  protected onEditableMouseUp(): void {
    this.captureCaretOffset();
  }

  protected onEditableKeyDown(event: KeyboardEvent): void {
    if (this.disabled()) return;
    if (event.key !== 'Backspace' && event.key !== 'Delete') {
      return;
    }

    const surface = this.editableSurface()?.nativeElement;
    if (!surface) return;

    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return;
    const range = sel.getRangeAt(0);
    if (!range.collapsed || !surface.contains(range.startContainer)) return;

    const adjacentChip =
      event.key === 'Backspace'
        ? this.findChipBeforeCaret(surface, range)
        : this.findChipAfterCaret(surface, range);

    if (!adjacentChip) return;

    event.preventDefault();
    const ordinal = Number(adjacentChip.dataset['tokenOrdinal']);
    if (Number.isNaN(ordinal)) return;

    const current = this.serializedValue();
    const segments = parseStepInstruction(current);
    const segmentIndices = segments
      .map((seg, idx) => (seg.token ? idx : -1))
      .filter((idx) => idx >= 0);
    const segmentIndex = segmentIndices[ordinal];
    if (segmentIndex === undefined) return;

    const beforeOffset = segments
      .slice(0, segmentIndex)
      .map((s) => s.rawToken || s.text)
      .join('').length;
    const next = replaceTokenSegmentAt(current, segmentIndex, null);
    this.applyProgrammaticValue(next, Math.min(beforeOffset, next.length));
  }

  protected onEditableClick(event: MouseEvent): void {
    if (this.disabled()) return;
    const target = event.target as HTMLElement | null;
    const editBtn = target?.closest('.chip-edit-btn') as HTMLElement | null;
    if (editBtn) {
      event.preventDefault();
      event.stopPropagation();
      const ordinal = Number(editBtn.dataset['tokenOrdinal']);
      if (!Number.isNaN(ordinal)) {
        this.editTokenAt(ordinal);
      }
      return;
    }

    const chip = target?.closest('.inline-step-chip') as HTMLElement | null;
    if (chip) {
      // Single click on chip body places caret right after the chip for smooth prose editing
      this.placeCaretAfterNode(chip);
      this.captureCaretOffset();
      return;
    }

    this.captureCaretOffset();
  }

  openInsertTokenDialog(mode: 'ingredient' | 'timer' | 'temperature'): void {
    if (this.disabled()) return;
    this.captureCaretOffset();
    const insertOffset = this.lastCaretOffset ?? this.serializedValue().length;
    const options = this.mergedIngredientOptions();

    const dialogRef = this.dialog.open<
      StepTokenDialogComponent,
      StepTokenDialogData,
      StepTokenDialogResult
    >(StepTokenDialogComponent, {
      data: {
        mode,
        availableIngredients: options.map((o) => o.displayName),
        availableIngredientOptions: options,
      },
      width: '540px',
    });

    dialogRef.afterClosed().subscribe((result) => {
      if (!result?.rawToken) return;
      const { nextInstruction, nextCaretOffset } = insertTokenAtOffset(
        this.serializedValue(),
        insertOffset,
        result.rawToken,
      );
      this.applyProgrammaticValue(nextInstruction, nextCaretOffset);
    });
  }

  editTokenAt(tokenOrdinal: number): void {
    if (this.disabled()) return;
    const current = this.serializedValue();
    const segments = parseStepInstruction(current);
    const segmentIndices = segments
      .map((seg, idx) => (seg.token ? idx : -1))
      .filter((idx) => idx >= 0);
    const segmentIndex = segmentIndices[tokenOrdinal];
    if (segmentIndex === undefined) return;

    const token = segments[segmentIndex]?.token;
    if (!token) return;

    const options = this.mergedIngredientOptions();
    const mode: 'ingredient' | 'timer' | 'temperature' =
      token.type === 'timer' || 'seconds' in token
        ? 'timer'
        : token.type === 'temperature' || 'fahrenheit' in token || 'celsius' in token
          ? 'temperature'
          : 'ingredient';

    const dialogRef = this.dialog.open<
      StepTokenDialogComponent,
      StepTokenDialogData,
      StepTokenDialogResult
    >(StepTokenDialogComponent, {
      data: {
        mode,
        token,
        availableIngredients: options.map((o) => o.displayName),
        availableIngredientOptions: options,
      },
      width: '540px',
    });

    dialogRef.afterClosed().subscribe((result) => {
      if (!result) return;
      const latest = this.serializedValue();
      const latestSegments = parseStepInstruction(latest);
      const latestIndices = latestSegments
        .map((seg, idx) => (seg.token ? idx : -1))
        .filter((idx) => idx >= 0);
      const latestSegmentIndex = latestIndices[tokenOrdinal];
      if (latestSegmentIndex === undefined) return;

      const beforeLen = latestSegments
        .slice(0, latestSegmentIndex)
        .map((s) => s.rawToken || s.text)
        .join('').length;

      if (result.delete) {
        const next = replaceTokenSegmentAt(latest, latestSegmentIndex, null);
        this.applyProgrammaticValue(next, Math.min(beforeLen, next.length));
      } else if (result.rawToken) {
        const next = replaceTokenSegmentAt(latest, latestSegmentIndex, result.rawToken);
        this.applyProgrammaticValue(next, beforeLen + result.rawToken.length);
      }
    });
  }

  private applyProgrammaticValue(nextValue: string, caretOffset: number | null): void {
    this.serializedValue.set(nextValue);
    this.onChange(nextValue);
    this.onTouched();
    if (this.viewInitialized) {
      this.renderSegmentsToDom(nextValue);
      if (caretOffset !== null) {
        this.lastCaretOffset = caretOffset;
        this.restoreCaretAtSerializedOffset(caretOffset);
      }
    }
  }

  private renderSegmentsToDom(value: string): void {
    const surface = this.editableSurface()?.nativeElement;
    if (!surface) return;

    surface.innerHTML = '';
    if (!value) return;

    const segments = parseStepInstruction(value);
    let tokenOrdinal = 0;
    for (const seg of segments) {
      if (seg.token) {
        surface.appendChild(
          this.createChipElement(seg.token, seg.rawToken || seg.text, tokenOrdinal),
        );
        tokenOrdinal += 1;
      } else {
        surface.appendChild(document.createTextNode(seg.rawToken || seg.text));
      }
    }
  }

  private createChipElement(
    token: StepToken,
    rawToken: string,
    tokenOrdinal: number,
  ): HTMLSpanElement {
    const chip = document.createElement('span');
    chip.setAttribute('contenteditable', 'false');
    chip.dataset['rawToken'] = rawToken;
    chip.dataset['tokenOrdinal'] = String(tokenOrdinal);

    let kindClass = 'is-ingredient';
    let iconName = 'nutrition';
    let labelText: string;
    let editPrefix = 'Edit ingredient';

    if (token.type === 'timer' || 'seconds' in token) {
      kindClass = 'is-timer';
      iconName = 'timer';
      labelText = formatTimerToken(token as StepTimerToken);
      editPrefix = 'Edit timer';
    } else if (token.type === 'temperature' || 'fahrenheit' in token || 'celsius' in token) {
      kindClass = 'is-temperature';
      iconName = 'device_thermostat';
      labelText = formatTemperatureToken(token as StepTemperatureToken, 'Original');
      editPrefix = 'Edit temperature';
    } else {
      labelText = formatTokenDisplay(token as StepIngredientToken, 1, 'Original');
    }

    chip.className = `inline-step-chip ${kindClass}`;

    const iconEl = document.createElement('span');
    iconEl.className = 'material-symbols-outlined chip-type-icon';
    iconEl.setAttribute('aria-hidden', 'true');
    iconEl.textContent = iconName;

    const labelEl = document.createElement('span');
    labelEl.className = 'chip-label';
    labelEl.textContent = labelText;

    const editBtn = document.createElement('button');
    editBtn.type = 'button';
    editBtn.className = 'chip-edit-btn';
    editBtn.tabIndex = -1;
    editBtn.dataset['tokenOrdinal'] = String(tokenOrdinal);
    editBtn.setAttribute('aria-label', `${editPrefix} ${labelText}`);

    const editIcon = document.createElement('span');
    editIcon.className = 'material-symbols-outlined chip-edit-icon';
    editIcon.setAttribute('aria-hidden', 'true');
    editIcon.textContent = 'edit';
    editBtn.appendChild(editIcon);

    chip.appendChild(iconEl);
    chip.appendChild(labelEl);
    chip.appendChild(editBtn);
    return chip;
  }

  private hasRawTokenInTextNodes(root: Node): boolean {
    for (const child of Array.from(root.childNodes)) {
      if (child.nodeType === Node.TEXT_NODE) {
        const text = child.textContent ?? '';
        if (text.includes('||') && parseStepInstruction(text).some((s) => !!s.token)) {
          return true;
        }
      } else if (child.nodeType === Node.ELEMENT_NODE) {
        const el = child as HTMLElement;
        if (!el.classList.contains('inline-step-chip') && this.hasRawTokenInTextNodes(el)) {
          return true;
        }
      }
    }
    return false;
  }

  private serializeDomToInstruction(surface: HTMLElement): string {
    return this.serializeContainerChildren(surface, false);
  }

  private serializeContainerChildren(container: HTMLElement, insideBlock: boolean): string {
    const children = Array.from(container.childNodes);
    let out = '';

    for (let i = 0; i < children.length; i++) {
      const child = children[i];
      if (child.nodeType === Node.TEXT_NODE) {
        out += (child.textContent ?? '').replace(/\u00A0/g, ' ');
      } else if (child.nodeType === Node.ELEMENT_NODE) {
        const el = child as HTMLElement;
        if (el.classList.contains('inline-step-chip')) {
          out += el.dataset['rawToken'] ?? '';
        } else if (el.tagName === 'BR') {
          const isTrailingBogusBr = i === children.length - 1;
          if (!isTrailingBogusBr) {
            out += '\n';
          }
        } else if (el.tagName === 'DIV' || el.tagName === 'P') {
          if (i > 0) {
            out += '\n';
          }
          out += this.serializeContainerChildren(el, true);
        } else {
          out += this.serializeContainerChildren(el, insideBlock);
        }
      }
    }

    return out;
  }

  private getSelectionOffsetInsideSurface(surface: HTMLElement): number | null {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return null;
    const range = sel.getRangeAt(0);
    if (!surface.contains(range.endContainer)) return null;

    let offset = 0;
    let found = false;

    const walkChildren = (container: HTMLElement): void => {
      const children = Array.from(container.childNodes);
      if (container === range.endContainer) {
        for (let i = 0; i < Math.min(range.endOffset, children.length); i++) {
          const c = children[i];
          if (c.nodeType === Node.TEXT_NODE) {
            offset += (c.textContent ?? '').replace(/\u00A0/g, ' ').length;
          } else if (c.nodeType === Node.ELEMENT_NODE) {
            offset += this.serializeSingleChildLength(c as HTMLElement, i > 0);
          }
        }
        found = true;
        return;
      }

      for (let i = 0; i < children.length; i++) {
        if (found) return;
        const c = children[i];
        if (c === range.endContainer) {
          if (c.nodeType === Node.TEXT_NODE) {
            const txt = (c.textContent ?? '').replace(/\u00A0/g, ' ');
            offset += Math.min(range.endOffset, txt.length);
            found = true;
            return;
          }
          if (c.nodeType === Node.ELEMENT_NODE) {
            const el = c as HTMLElement;
            if (el.classList.contains('inline-step-chip')) {
              offset += (el.dataset['rawToken'] ?? '').length;
              found = true;
              return;
            }
            if ((el.tagName === 'DIV' || el.tagName === 'P') && i > 0) {
              offset += 1;
            }
            walkChildren(el);
            return;
          }
        }

        if (c.nodeType === Node.TEXT_NODE) {
          offset += (c.textContent ?? '').replace(/\u00A0/g, ' ').length;
        } else if (c.nodeType === Node.ELEMENT_NODE) {
          const el = c as HTMLElement;
          if (el.classList.contains('inline-step-chip')) {
            offset += (el.dataset['rawToken'] ?? '').length;
            if (el.contains(range.endContainer)) {
              found = true;
              return;
            }
          } else {
            if ((el.tagName === 'DIV' || el.tagName === 'P') && i > 0) {
              offset += 1;
            }
            if (el.contains(range.endContainer)) {
              walkChildren(el);
              return;
            }
            offset += this.serializeContainerChildren(el, true).length;
          }
        }
      }
    };

    walkChildren(surface);
    return offset;
  }

  private serializeSingleChildLength(el: HTMLElement, nonFirstBlock: boolean): number {
    if (el.classList.contains('inline-step-chip')) {
      return (el.dataset['rawToken'] ?? '').length;
    }
    if (el.tagName === 'BR') {
      return 1;
    }
    const prefix = (el.tagName === 'DIV' || el.tagName === 'P') && nonFirstBlock ? 1 : 0;
    return prefix + this.serializeContainerChildren(el, true).length;
  }

  private restoreCaretAtSerializedOffset(targetOffset: number): void {
    const surface = this.editableSurface()?.nativeElement;
    if (!surface) return;

    const sel = window.getSelection();
    if (!sel) return;

    surface.focus({ preventScroll: true });
    const range = document.createRange();
    let remaining = Math.max(0, targetOffset);
    let placed = false;

    for (const child of Array.from(surface.childNodes)) {
      if (child.nodeType === Node.TEXT_NODE) {
        const len = (child.textContent ?? '').length;
        if (remaining <= len) {
          range.setStart(child, remaining);
          range.collapse(true);
          placed = true;
          break;
        }
        remaining -= len;
      } else if (child.nodeType === Node.ELEMENT_NODE) {
        const el = child as HTMLElement;
        if (el.classList.contains('inline-step-chip')) {
          const len = (el.dataset['rawToken'] ?? '').length;
          if (remaining <= 0) {
            range.setStartBefore(el);
            range.collapse(true);
            placed = true;
            break;
          }
          if (remaining < len) {
            range.setStartAfter(el);
            range.collapse(true);
            placed = true;
            break;
          }
          remaining -= len;
        }
      }
    }

    if (!placed) {
      range.selectNodeContents(surface);
      range.collapse(false);
    }

    sel.removeAllRanges();
    sel.addRange(range);
  }

  private findChipBeforeCaret(surface: HTMLElement, range: Range): HTMLElement | null {
    if (range.startContainer === surface && range.startOffset > 0) {
      const prev = surface.childNodes[range.startOffset - 1] as HTMLElement | undefined;
      return prev?.nodeType === Node.ELEMENT_NODE && prev.classList.contains('inline-step-chip')
        ? prev
        : null;
    }
    if (range.startContainer.nodeType === Node.TEXT_NODE && range.startOffset === 0) {
      const prev = range.startContainer.previousSibling as HTMLElement | null;
      return prev?.nodeType === Node.ELEMENT_NODE && prev.classList.contains('inline-step-chip')
        ? prev
        : null;
    }
    return null;
  }

  private findChipAfterCaret(surface: HTMLElement, range: Range): HTMLElement | null {
    if (range.startContainer === surface && range.startOffset < surface.childNodes.length) {
      const next = surface.childNodes[range.startOffset] as HTMLElement | undefined;
      return next?.nodeType === Node.ELEMENT_NODE && next.classList.contains('inline-step-chip')
        ? next
        : null;
    }
    if (
      range.startContainer.nodeType === Node.TEXT_NODE &&
      range.startOffset === (range.startContainer.textContent ?? '').length
    ) {
      const next = range.startContainer.nextSibling as HTMLElement | null;
      return next?.nodeType === Node.ELEMENT_NODE && next.classList.contains('inline-step-chip')
        ? next
        : null;
    }
    return null;
  }

  private placeCaretAfterNode(node: HTMLElement): void {
    const sel = window.getSelection();
    if (!sel) return;
    const range = document.createRange();
    range.setStartAfter(node);
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
  }
}
