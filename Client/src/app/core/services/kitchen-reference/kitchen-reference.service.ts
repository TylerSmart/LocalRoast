import { Service, inject, signal } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import { KitchenReferenceTab } from '../../utils/kitchen-reference-data';
import { KitchenReferenceDialogComponent } from '../../../features/kitchen-reference-dialog/kitchen-reference-dialog.component';

const TAB_STORAGE_KEY = `${APP_STORAGE_PREFIX}-kitchen-ref-tab`;
const VALID_TABS: readonly KitchenReferenceTab[] = [
  'convert',
  'temps',
  'glossary',
  'swaps',
  'ratios',
];

export interface OpenKitchenReferenceOptions {
  tab?: KitchenReferenceTab;
  query?: string;
}

@Service()
export class KitchenReferenceService {
  private readonly dialog = inject(MatDialog, { optional: true });

  readonly activeTab = signal<KitchenReferenceTab>(this.readStoredTab());
  readonly searchQuery = signal<string>('');

  setActiveTab(tab: KitchenReferenceTab): void {
    this.activeTab.set(tab);
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(TAB_STORAGE_KEY, tab);
    }
  }

  setSearchQuery(query: string): void {
    this.searchQuery.set(query);
  }

  isOpen(): boolean {
    if (!this.dialog) {
      return false;
    }
    return this.dialog.openDialogs.some(
      (d) => d.componentInstance instanceof KitchenReferenceDialogComponent,
    );
  }

  openDialog(
    options?: OpenKitchenReferenceOptions,
  ): MatDialogRef<KitchenReferenceDialogComponent> | undefined {
    if (!this.dialog) {
      return undefined;
    }

    if (options?.tab && VALID_TABS.includes(options.tab)) {
      this.setActiveTab(options.tab);
    }
    if (options?.query !== undefined) {
      this.searchQuery.set(options.query);
    }

    const existing = this.dialog.openDialogs.find(
      (d) => d.componentInstance instanceof KitchenReferenceDialogComponent,
    );
    if (existing) {
      return existing as MatDialogRef<KitchenReferenceDialogComponent>;
    }

    return this.dialog.open(KitchenReferenceDialogComponent, {
      width: '780px',
      maxWidth: '95vw',
      panelClass: 'kitchen-reference-dialog-panel',
      autoFocus: false,
    });
  }

  toggleDialog(): void {
    if (!this.dialog) {
      return;
    }
    const existing = this.dialog.openDialogs.find(
      (d) => d.componentInstance instanceof KitchenReferenceDialogComponent,
    );
    if (existing) {
      existing.close();
      return;
    }
    this.openDialog();
  }

  private readStoredTab(): KitchenReferenceTab {
    if (typeof localStorage === 'undefined') {
      return 'convert';
    }
    const stored = localStorage.getItem(TAB_STORAGE_KEY) as KitchenReferenceTab | null;
    return stored && VALID_TABS.includes(stored) ? stored : 'convert';
  }
}
