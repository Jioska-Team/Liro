import { afterNextRender, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { CatalogItem, BundleContents, CatalogService } from './core/api/catalog';
@Component({ selector: 'app-root', imports: [], templateUrl: './app.html', styleUrl: './app.css' })
export class App {
 private readonly catalog = inject(CatalogService);
 private componentsRequest?: Subscription;
 private readonly destroyRef = inject(DestroyRef);
 protected readonly items = signal<CatalogItem[]>([]);
 protected readonly catalogLoading = signal(false);
 protected readonly catalogError = signal('');
 protected readonly page = signal(1);
 protected readonly hasMore = signal(false);
 protected readonly selectedBundle = signal<CatalogItem | null>(null);
 protected readonly contents = signal<BundleContents | null>(null);
 protected readonly componentsError = signal('');
 protected readonly connected = signal(false);
 constructor() {
  afterNextRender(() => this.loadPage(1));
 }
 protected loadPage(page: number) {
  if (page < 1 || this.catalogLoading()) return;
  this.catalogLoading.set(true); this.catalogError.set(''); this.closeComponents();
  this.catalog.getPage(page).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
   next: result => {
    this.connected.set(true);
    this.items.set(result.items.filter(item => item.isCatalogVisible !== false &&
     (item.itemType === 'Bundle' || ![17,27,28,29,30,31,79].includes(item.assetTypeId ?? 0))));
    this.page.set(page); this.hasMore.set(result.hasMore); this.catalogLoading.set(false);
   },
   error: () => { this.connected.set(false); this.catalogError.set('No se pudo cargar el catálogo. Inténtalo de nuevo.'); this.catalogLoading.set(false); }
  });
 }
 protected closeComponents() {
  this.componentsRequest?.unsubscribe();
  this.componentsRequest = undefined;
  this.selectedBundle.set(null);
  this.contents.set(null);
  this.componentsError.set('');
 }
 protected showComponents(item: CatalogItem) {
  if (this.selectedBundle()?.id === item.id &&
      (this.contents() !== null || (this.componentsRequest && !this.componentsRequest.closed))) return;
  this.componentsRequest?.unsubscribe();
  this.selectedBundle.set(item); this.contents.set(null); this.componentsError.set('');
  this.componentsRequest = this.catalog.getComponents(item.robloxItemId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
   next: result => { if (this.selectedBundle()?.id === item.id) this.contents.set(result); },
   error: () => { if (this.selectedBundle()?.id === item.id) this.componentsError.set('No se pudieron consultar los componentes.'); }
  });
 }
}
