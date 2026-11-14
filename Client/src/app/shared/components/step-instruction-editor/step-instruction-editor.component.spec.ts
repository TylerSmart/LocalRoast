import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { StepInstructionEditorComponent } from './step-instruction-editor.component';
import { StepTokenDialogResult } from '../step-token-dialog/step-token-dialog.component';

describe('StepInstructionEditorComponent', () => {
  let component: StepInstructionEditorComponent;
  let fixture: ComponentFixture<StepInstructionEditorComponent>;
  let mockDialog: { open: ReturnType<typeof vi.fn> };

  function setDialogResult(result: StepTokenDialogResult | undefined) {
    mockDialog.open.mockReturnValue({
      afterClosed: () => of(result),
    } as unknown as MatDialogRef<unknown>);
  }

  function getSurface(): HTMLDivElement {
    return fixture.nativeElement.querySelector('.step-instruction-editable') as HTMLDivElement;
  }

  beforeEach(async () => {
    mockDialog = {
      open: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [StepInstructionEditorComponent],
      providers: [{ provide: MatDialog, useValue: mockDialog }],
    }).compileComponents();

    fixture = TestBed.createComponent(StepInstructionEditorComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders plain text and atomic interactive chips from writeValue', () => {
    const raw =
      'Heat ||{"type":"temperature","fahrenheit":350}|| and melt ||{"ingredient":"Butter","amount":2,"unit":"tbsp"}|| for ||{"type":"timer","seconds":120}||.';
    component.writeValue(raw);
    fixture.detectChanges();

    const chips = getSurface().querySelectorAll('.inline-step-chip');
    expect(chips.length).toBe(3);
    expect(chips[0].classList.contains('is-temperature')).toBe(true);
    expect(chips[1].classList.contains('is-ingredient')).toBe(true);
    expect(chips[2].classList.contains('is-timer')).toBe(true);
    expect(fixture.nativeElement.getAttribute('data-serialized-value')).toBe(raw);
  });

  it('emits typed prose and auto-upgrades raw token text pasted into the surface', () => {
    const emitted: string[] = [];
    component.registerOnChange((v) => emitted.push(v));

    const surface = getSurface();
    surface.textContent = 'Add ||{"ingredient":"Eggs","amount":2,"unit":"whole"}|| and whisk.';
    surface.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(emitted.at(-1)).toBe(
      'Add ||{"ingredient":"Eggs","amount":2,"unit":"whole"}|| and whisk.',
    );
    expect(surface.querySelectorAll('.inline-step-chip').length).toBe(1);
    expect(surface.querySelector('.chip-label')?.textContent).toContain('2 Eggs');
  });

  it('inserts new token at last caret offset via dialog', () => {
    const emitted: string[] = [];
    component.registerOnChange((v) => emitted.push(v));
    component.writeValue('Simmer sauce');
    fixture.detectChanges();

    setDialogResult({
      rawToken: '||{"type":"timer","seconds":300,"label":"Simmer"}||',
    });

    component.openInsertTokenDialog('timer');
    fixture.detectChanges();

    expect(mockDialog.open).toHaveBeenCalled();
    expect(emitted.at(-1)).toBe(
      'Simmer sauce ||{"type":"timer","seconds":300,"label":"Simmer"}|| ',
    );
    expect(getSurface().querySelectorAll('.inline-step-chip.is-timer').length).toBe(1);
  });

  it('updates and deletes exact chip via edit button dialog', () => {
    const token = '||{"ingredient":"Butter","amount":1,"unit":"tbsp"}||';
    component.writeValue(`Melt ${token}, then swirl in ${token}.`);
    fixture.detectChanges();

    const emitted: string[] = [];
    component.registerOnChange((v) => emitted.push(v));

    setDialogResult({
      rawToken: '||{"ingredient":"Butter","amount":3,"unit":"tbsp"}||',
    });

    const editButtons = getSurface().querySelectorAll('.chip-edit-btn');
    expect(editButtons.length).toBe(2);
    (editButtons[1] as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(emitted.at(-1)).toBe(
      `Melt ${token}, then swirl in ||{"ingredient":"Butter","amount":3,"unit":"tbsp"}||.`,
    );

    setDialogResult({ delete: true });
    (getSurface().querySelectorAll('.chip-edit-btn')[0] as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(emitted.at(-1)).toBe(
      'Melt, then swirl in ||{"ingredient":"Butter","amount":3,"unit":"tbsp"}||.',
    );
  });
});
